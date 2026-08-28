using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using ZwSoft.ZwCAD.DatabaseServices;

namespace ZwcadBatchPlot;

/// <summary>
/// 中望CAD 自动加载管理器 — 通过注册表实现插件随当前中望CAD 启动自动加载。
/// 对齐 IFoxCAD：只写入 <c>HostApplicationServices.Current.UserRegistryProductRootKey\Applications</c>，
/// 不扫其它年份版本。Applications 不存在时按 IFoxCAD 方式 CreateSubKey 创建。
/// </summary>
public static class AutoloadManager
{
    /// <summary>注册表中的应用键名</summary>
    private const string AppKeyName = "ZwcadBatchPlot";
    /// <summary>注册表中显示的应用描述</summary>
    private const string AppDescription = "中望CAD批量打印插件";

    /// <summary>当前 DLL 的完整路径</summary>
    public static string CurrentDllPath => Assembly.GetExecutingAssembly().Location;

    /// <summary>
    /// 安装自动加载：仅对当前正在运行的中望CAD 写入 LOADER / LOADCTRLS / MANAGED。
    /// </summary>
    /// <param name="dllPath">可选，指定要注册的 DLL 路径；为空则使用当前程序集路径</param>
    /// <returns>注册到的 Applications 根路径列表</returns>
    public static IReadOnlyList<string> Install(string? dllPath = null)
    {
        dllPath = string.IsNullOrWhiteSpace(dllPath) ? CurrentDllPath : Path.GetFullPath(dllPath);
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: true)
            ?? throw new InvalidOperationException("未找到当前中望CAD自加载注册表位置。请先启动一次中望CAD。");

        if (!WriteAutoloadKey(applicationsRoot, dllPath))
        {
            throw new InvalidOperationException("无法写入当前中望CAD的自动加载注册表项。");
        }

        return new[] { applicationsRoot };
    }

    /// <summary>
    /// 卸载自动加载：仅删除当前正在运行的中望CAD 下的注册表子键。
    /// </summary>
    /// <returns>成功清理的注册表项数量</returns>
    public static int Uninstall()
    {
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
        if (applicationsRoot == null)
        {
            return 0;
        }

        using var parent = Registry.CurrentUser.OpenSubKey(applicationsRoot, writable: true);
        if (parent == null)
        {
            return 0;
        }

        try
        {
            parent.DeleteSubKeyTree(AppKeyName, throwOnMissingSubKey: false);
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 检测当前中望CAD 是否已安装自动加载。
    /// </summary>
    /// <param name="dllPath">输出已注册的 DLL 路径</param>
    /// <returns>是否已安装自动加载</returns>
    public static bool IsInstalled(out string dllPath)
    {
        dllPath = "";
        var applicationsRoot = GetCurrentCadApplicationsRoot(createIfMissing: false);
        if (applicationsRoot == null)
        {
            return false;
        }

        using var key = Registry.CurrentUser.OpenSubKey(applicationsRoot + "\\" + AppKeyName);
        var loader = key?.GetValue("LOADER")?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(loader))
        {
            return false;
        }

        dllPath = loader;
        return true;
    }

    /// <summary>
    /// 获取当前正在运行的中望CAD 的 Applications 路径（IFoxCAD <c>GetAcAppKey</c> 写法）。
    /// </summary>
    /// <param name="createIfMissing">为 true 时创建尚不存在的 Applications</param>
    /// <returns>HKCU 相对路径；无法定位当前 CAD 时返回 null</returns>
    private static string? GetCurrentCadApplicationsRoot(bool createIfMissing)
    {
        var productRoot = HostApplicationServices.Current?.UserRegistryProductRootKey;
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            return null;
        }

        productRoot = NormalizeHkcuRelativePath(productRoot!);
        if (string.IsNullOrWhiteSpace(productRoot))
        {
            return null;
        }

        var applicationsPath = productRoot + "\\Applications";
        if (createIfMissing)
        {
            using var created = Registry.CurrentUser.CreateSubKey(applicationsPath);
            return created != null ? applicationsPath : null;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(applicationsPath);
        return existing != null ? applicationsPath : null;
    }

    /// <summary>
    /// 在指定 Applications 根下写入当前插件的 DemandLoad 键值。
    /// </summary>
    /// <param name="applicationsRoot">Applications 注册表路径</param>
    /// <param name="dllPath">LOADER 指向的 DLL</param>
    /// <returns>是否写入成功</returns>
    private static bool WriteAutoloadKey(string applicationsRoot, string dllPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(applicationsRoot + "\\" + AppKeyName);
        if (key == null)
        {
            return false;
        }

        key.SetValue("DESCRIPTION", AppDescription, RegistryValueKind.String);
        key.SetValue("LOADCTRLS", 2, RegistryValueKind.DWord);
        key.SetValue("LOADER", dllPath, RegistryValueKind.String);
        key.SetValue("MANAGED", 1, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>
    /// 将 <c>UserRegistryProductRootKey</c> 规范为 HKCU 相对路径。
    /// </summary>
    /// <param name="path">CAD 返回的产品根路径</param>
    /// <returns>不含 HKCU 前缀的相对路径</returns>
    private static string NormalizeHkcuRelativePath(string path)
    {
        var normalized = path.Replace('/', '\\').Trim('\\');
        const string hkcuPrefix = @"HKEY_CURRENT_USER\";
        if (normalized.StartsWith(hkcuPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(hkcuPrefix.Length).Trim('\\');
        }

        return normalized;
    }
}
