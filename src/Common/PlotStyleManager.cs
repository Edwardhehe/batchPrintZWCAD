using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.DatabaseServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 提供打印样式列表和编辑入口，保证三个打印窗口使用同一套 CTB 查找规则。
/// </summary>
internal static class PlotStyleManager
{
    public static IReadOnlyList<string> GetAvailableCtbStyles()
    {
        return PlotSettingsValidator.Current.GetPlotStyleSheetList()
            .Cast<object>()
            .Select(value => value?.ToString() ?? "")
            .Where(value => value.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void EditSelectedStyle(IWin32Window owner, string? styleSheet)
    {
        var selectedStyle = styleSheet ?? "";
        if (string.IsNullOrWhiteSpace(selectedStyle))
        {
            MessageBox.Show(
                owner,
                "请先选择一个打印样式。",
                "打印样式设置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var stylePath = ResolveStylePath(selectedStyle) ?? "";
        if (string.IsNullOrWhiteSpace(stylePath))
        {
            MessageBox.Show(
                owner,
                $"未找到打印样式文件“{selectedStyle}”。请检查当前 CAD 的打印样式搜索路径。",
                "打印样式设置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        try
        {
            // CTB 文件由 CAD 安装程序注册到打印样式表编辑器，直接打开即可修改当前选中的样式。
            Process.Start(new ProcessStartInfo
            {
                FileName = stylePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            if (TryRevealStyleFile(stylePath))
            {
                MessageBox.Show(
                    owner,
                    $"无法直接启动打印样式表编辑器，已在资源管理器中定位“{selectedStyle}”。\n请双击该文件进行修改。\n\n{ex.Message}",
                    "打印样式设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            MessageBox.Show(
                owner,
                $"无法打开打印样式“{selectedStyle}”。\n\n{ex.Message}",
                "打印样式设置",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string? ResolveStylePath(string styleSheet)
    {
        var document = CadApp.DocumentManager.MdiActiveDocument;
        if (document != null)
        {
            try
            {
                // 让当前 CAD 按自身配置的打印样式搜索路径解析，避免硬编码版本或用户目录。
                var resolved = HostApplicationServices.Current.FindFile(
                    styleSheet,
                    document.Database,
                    FindFileHint.Default);
                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                {
                    return Path.GetFullPath(resolved);
                }
            }
            catch
            {
                // 部分 CAD 版本不会通过 FindFile 返回 CTB，继续检查当前用户配置目录。
            }
        }

        foreach (var directory in GetCandidateStyleDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, styleSheet);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // 单个无效配置目录不应阻止检查其它候选目录。
            }
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateStyleDirectories()
    {
        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        if (!string.IsNullOrWhiteSpace(plottersDirectory))
        {
            yield return plottersDirectory;
            yield return Path.Combine(plottersDirectory, "Plot Styles");
        }

        var configuredStyleDirectory = GetSystemVariableString("PrinterStyleSheetDir");
        if (!string.IsNullOrWhiteSpace(configuredStyleDirectory))
        {
            yield return configuredStyleDirectory;
        }

        var roamableRoot = GetSystemVariableString("ROAMABLEROOTPREFIX");
        if (!string.IsNullOrWhiteSpace(roamableRoot))
        {
            // AutoCAD 与 ZWCAD 的当前用户打印样式目录名称不同，均从宿主返回的根目录派生。
            yield return Path.Combine(roamableRoot, "Plotters", "Plot Styles");
            yield return Path.Combine(roamableRoot, "Printstyle");
        }
    }

    private static string GetSystemVariableString(string name)
    {
        try
        {
            return CadApp.GetSystemVariable(name)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool TryRevealStyleFile(string stylePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(stylePath)}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
