using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace ZwcadBatchPlot;

/// <summary>
/// 中望CAD 自动加载管理器 — 通过注册表实现插件随中望CAD 启动自动加载。
/// 在 HKCU\Software\ZWSOFT\ZWCAD\{版本}\{语言}\Applications 下写入注册表项。
/// </summary>
public static class AutoloadManager
{
    /// <summary>注册表中的应用键名</summary>
    private const string AppKeyName = "ZwcadBatchPlot";
    /// <summary>注册表中显示的应用描述</summary>
    private const string AppDescription = "中望CAD批量打印插件";
    /// <summary>中望CAD 注册表根路径</summary>
    private const string ZwcadRoot = @"Software\ZWSOFT\ZWCAD";

    /// <summary>当前 DLL 的完整路径</summary>
    public static string CurrentDllPath => Assembly.GetExecutingAssembly().Location;

    /// <summary>
    /// 安装自动加载：扫描所有中望CAD 版本的 Applications 注册表路径，
    /// 写入 LOADER / LOADCTRLS / MANAGED 等键值。
    /// </summary>
    /// <param name="dllPath">可选，指定要注册的 DLL 路径；为空则使用当前程序集路径</param>
    /// <returns>注册到的 Applications 根路径列表</returns>
    public static IReadOnlyList<string> Install(string? dllPath = null)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        // 扫描所有中望CAD 版本的 Applications 注册表路径
        var roots = GetApplicationRoots().ToList();
        if (roots.Count == 0)
        {
            throw new InvalidOperationException("未找到中望CAD自加载注册表位置。请先启动一次中望CAD 2025。");
        }

        // 在每个版本的 Applications 下写入自加载注册表项
        foreach (var applicationsRoot in roots)
        {
            using var key = Registry.CurrentUser.CreateSubKey(applicationsRoot + "\\" + AppKeyName);
            if (key == null)
            {
                continue;
            }

            // DESCRIPTION: 插件描述
            key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
            // LOADCTRLS: 2 = 在 CAD 启动时加载
            key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
            // LOADER: 要加载的 DLL 路径
            key.SetValue("LOADER", dllPath, RegistryValueKind.String);
            // MANAGED: 1 = .NET 托管程序集
            key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
        }

        return roots;
    }

    /// <summary>
    /// 卸载自动加载：遍历所有中望CAD 版本，删除 Applications 下的注册表子键。
    /// </summary>
    /// <returns>成功清理的注册表项数量</returns>
    public static int Uninstall()
    {
        var removed = 0;
        foreach (var applicationsRoot in GetApplicationRoots())
        {
            using var parent = Registry.CurrentUser.OpenSubKey(applicationsRoot, writable: true);
            if (parent == null)
            {
                continue;
            }

            try
            {
                parent.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
                removed++;
            }
            catch
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// 检测自动加载是否已安装：检查注册表 Applications 下是否存在 LOADER 值。
    /// </summary>
    /// <param name="dllPath">输出已注册的 DLL 路径</param>
    /// <returns>是否已安装自动加载</returns>
    public static bool IsInstalled(out string dllPath)
    {
        dllPath = "";
        foreach (var applicationsRoot in GetApplicationRoots())
        {
            using var key = Registry.CurrentUser.OpenSubKey(applicationsRoot + "\\" + AppKeyName);
            var loader = key?.GetValue("LOADER")?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(loader))
            {
                dllPath = loader;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 枚举所有已安装中望CAD 版本的 Applications 注册表路径。
    /// 注册表结构：HKCU\Software\ZWSOFT\ZWCAD\{版本}\{语言}\Applications
    /// </summary>
    private static IEnumerable<string> GetApplicationRoots()
    {
        using var root = Registry.CurrentUser.OpenSubKey(ZwcadRoot);
        if (root == null)
        {
            yield break;
        }

        // 注册表结构：
        // HKCU\Software\ZWSOFT\ZWCAD\{版本号}\{语言代号}\Applications
        foreach (var version in root.GetSubKeyNames().OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase))
        {
            using var versionKey = root.OpenSubKey(version);
            if (versionKey == null)
            {
                continue;
            }

            foreach (var locale in versionKey.GetSubKeyNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var applicationsPath = ZwcadRoot + "\\" + version + "\\" + locale + "\\Applications";
                using var applications = Registry.CurrentUser.OpenSubKey(applicationsPath);
                if (applications != null)
                {
                    yield return applicationsPath;
                }
            }
        }
    }
}
