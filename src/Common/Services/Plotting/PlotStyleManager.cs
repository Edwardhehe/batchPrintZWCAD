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

    /// <summary>
    /// 取出 CTB 文件名（去掉路径），便于比较 CAD 列表与设置里保存的值。
    /// </summary>
    public static string NormalizeStyleName(string? styleSheet)
    {
        var name = (styleSheet ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            return "";
        }

        try
        {
            name = Path.GetFileName(name);
        }
        catch
        {
            // 含非法路径字符时仍用原始文本比较。
        }

        return name.Trim();
    }

    /// <summary>
    /// 判断两个打印样式是否为同一份 CTB，忽略路径、扩展名和大小写。
    /// </summary>
    public static bool StyleNamesEqual(string? left, string? right)
    {
        var a = NormalizeStyleName(left);
        var b = NormalizeStyleName(right);
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(
            Path.GetFileNameWithoutExtension(a),
            Path.GetFileNameWithoutExtension(b),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 在 CAD 当前可用的 CTB 列表中查找与上次保存值相同的项。
    /// </summary>
    public static string? FindSavedStyle(IEnumerable<string> styles, string? savedStyleSheet)
    {
        var saved = NormalizeStyleName(savedStyleSheet);
        if (string.IsNullOrEmpty(saved))
        {
            return null;
        }

        return styles.FirstOrDefault(value => StyleNamesEqual(value, saved));
    }

    /// <summary>
    /// 把上次保存的 CTB 选回下拉框。已保存过时绝不回落到 monochrome，避免把用户选择冲掉。
    /// 列表里暂时没有该文件时，把保存值插到第一项，保证界面仍显示用户上次的选择。
    /// </summary>
    public static void RestoreSavedStyle(ComboBox combo, string? savedStyleSheet)
    {
        var saved = NormalizeStyleName(savedStyleSheet);
        if (TrySelectStyle(combo, saved))
        {
            return;
        }

        if (!string.IsNullOrEmpty(saved))
        {
            combo.Items.Insert(0, saved);
            combo.SelectedIndex = 0;
            return;
        }

        if (TrySelectStyle(combo, "monochrome.ctb") || TrySelectContaining(combo, "monochrome"))
        {
            return;
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static bool TrySelectStyle(ComboBox combo, string saved)
    {
        if (string.IsNullOrEmpty(saved))
        {
            return false;
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (StyleNamesEqual(combo.Items[i]?.ToString(), saved))
            {
                combo.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private static bool TrySelectContaining(ComboBox combo, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i]?.ToString()?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                combo.SelectedIndex = i;
                return true;
            }
        }

        return false;
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
