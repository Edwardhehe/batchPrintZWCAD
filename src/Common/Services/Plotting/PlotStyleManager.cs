using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.PlottingServices;
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
            // “打印对象线宽”生成的临时副本（*__objlw.ctb）只供出图内部使用，不出现在下拉框中。
            .Where(value => !PlotStyleLineweightConverter.IsObjectLineweightCopy(value))
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
    /// 解析作业实际使用的打印样式：作业自带 StyleSheet 时优先，否则回退 fallback（通常为主窗体当前样式）。
    /// </summary>
    public static string ResolveJobStyle(PlotJob? job, string? fallback)
    {
        if (job != null && !string.IsNullOrWhiteSpace(job.StyleSheet))
        {
            return NormalizeStyleName(job.StyleSheet);
        }

        return NormalizeStyleName(fallback);
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
    /// 在可用 CTB 列表中解析应使用的样式：优先上次保存值；找不到则回退 monochrome，再回退第一项。
    /// 用于下拉框恢复与无 UI 的单张打印默认值，避免删掉样式或换 CAD 版本后仍记住失效 CTB。
    /// </summary>
    public static string ResolvePreferredStyle(IEnumerable<string> styles, string? savedStyleSheet)
    {
        var list = styles as IList<string> ?? styles.ToList();
        var matched = FindSavedStyle(list, savedStyleSheet);
        if (!string.IsNullOrEmpty(matched))
        {
            return matched!;
        }

        var monochrome = list.FirstOrDefault(value =>
            value.IndexOf("monochrome", StringComparison.OrdinalIgnoreCase) >= 0);
        if (!string.IsNullOrEmpty(monochrome))
        {
            return monochrome!;
        }

        return list.FirstOrDefault() ?? "";
    }

    /// <summary>
    /// 把上次保存的 CTB 选回下拉框；当前 CAD 列表中不存在时，改选已有可用样式（优先 monochrome）。
    /// </summary>
    /// <returns>实际选中的样式名；无可用项时为空。</returns>
    public static string RestoreSavedStyle(ComboBox combo, string? savedStyleSheet)
    {
        var available = combo.Items
            .Cast<object>()
            .Select(item => item?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var preferred = ResolvePreferredStyle(available, savedStyleSheet);
        if (string.IsNullOrEmpty(preferred))
        {
            return "";
        }

        if (TrySelectStyle(combo, preferred))
        {
            return combo.SelectedItem?.ToString() ?? preferred;
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
            return combo.SelectedItem?.ToString() ?? "";
        }

        return "";
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

    public static void EditSelectedStyle(Window owner, string? styleSheet)
    {
        var selectedStyle = styleSheet ?? "";
        if (string.IsNullOrWhiteSpace(selectedStyle))
        {
            MessageBox.Show(
                owner,
                "请先选择一个打印样式。",
                "打印样式设置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var stylePath = ResolveStylePath(selectedStyle) ?? "";
        if (!string.IsNullOrWhiteSpace(stylePath) && TryOpenStyleFile(stylePath))
        {
            return;
        }

        // 找不到磁盘文件，或 .ctb 未关联编辑器时，交给 CAD 自己的打印样式管理器。
        if (TryOpenCadPlotStyleManager())
        {
            var hint = string.IsNullOrWhiteSpace(stylePath)
                ? $"未在当前 CAD 的打印样式搜索路径中定位“{selectedStyle}”。已打开 CAD 打印样式管理器，请双击该文件进行修改。"
                : $"无法直接启动打印样式表编辑器。已打开 CAD 打印样式管理器，请双击“{selectedStyle}”进行修改。";
            MessageBox.Show(
                owner,
                hint,
                "打印样式设置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            owner,
            $"无法打开打印样式“{selectedStyle}”。请在 CAD 中运行 STYLESMANAGER，确认选项里的打印样式表搜索路径。",
            "打印样式设置",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    /**
     * ResolveStylePath：向 CAD 要这份样式表的完整路径。
     * 优先用 PlotConfigManager 的 FullPath（与下拉列表同源）；读不到时再按选项里的样式表搜索路径找文件。
     */
    internal static string? ResolveStylePath(string styleSheet)
    {
        var raw = (styleSheet ?? "").Trim();
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            if (Path.IsPathRooted(raw) && File.Exists(raw))
            {
                return Path.GetFullPath(raw);
            }
        }
        catch
        {
            // 非法路径继续按文件名向 CAD 查询。
        }

        var fileName = NormalizeStyleName(raw);
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }

        var fromPlotConfig = TryResolveStylePathFromPlotConfig(fileName);
        if (!string.IsNullOrWhiteSpace(fromPlotConfig))
        {
            return fromPlotConfig;
        }

        foreach (var directory in AcadPlotterInstaller.GetPlotStyleSearchDirectories())
        {
            try
            {
                var candidate = Path.Combine(directory, fileName);
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

        // 图纸目录等支持路径中的 CTB，CAD 打印时也能用；样式表搜索路径未包含时再问 FindFile。
        return TryFindStyleFileWithCad(fileName);
    }

    /**
     * TryResolveStylePathFromPlotConfig：用 PlotConfigManager 取样式表完整路径。
     * ColorDependentPlotStyles / NamedPlotStyles 与 GetPlotStyleSheetList 同源，FullPath 是 CAD 实际定位到的文件，不拼 Plotters 目录。
     */
    private static string? TryResolveStylePathFromPlotConfig(string fileName)
    {
        try
        {
            PlotConfigManager.RefreshList(RefreshCode.RefreshStyleList);
        }
        catch
        {
            // 刷新失败时仍读取当前已缓存的样式表列表。
        }

        try
        {
            var styles = fileName.EndsWith(".stb", StringComparison.OrdinalIgnoreCase)
                ? PlotConfigManager.NamedPlotStyles
                : PlotConfigManager.ColorDependentPlotStyles;

            // 当前这套 API 的列表元素是字符串（文件名或完整路径）；若元素是 PlotConfigInfo，则用它的 FullPath。
            foreach (var entry in (IEnumerable)styles)
            {
                if (entry is string text)
                {
                    var resolved = MatchExistingStyleFile(text, fileName);
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }

                    continue;
                }

                if (entry is PlotConfigInfo info)
                {
                    string? resolved = null;
                    try
                    {
                        resolved = MatchExistingStyleFile(info.FullPath, fileName)
                            ?? MatchExistingStyleFile(info.DeviceName, fileName);
                    }
                    catch
                    {
                        // 单条记录读失败时继续看下一条。
                    }

                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        return resolved;
                    }
                }
            }
        }
        catch
        {
            // 当前宿主读不到样式表列表时，改走选项中的搜索路径。
        }

        return null;
    }

    /**
     * MatchExistingStyleFile：条目与目标样式同名，且本身是已存在的绝对路径时返回该文件。
     */
    private static string? MatchExistingStyleFile(string? entry, string fileName)
    {
        if (string.IsNullOrWhiteSpace(entry) || !StyleNamesEqual(entry, fileName))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathRooted(entry) || !File.Exists(entry))
            {
                return null;
            }

            return Path.GetFullPath(entry);
        }
        catch
        {
            return null;
        }
    }

    /** TryFindStyleFileWithCad：让当前 CAD 按自身支持文件搜索解析 CTB 文件名。 */
    private static string? TryFindStyleFileWithCad(string fileName)
    {
        var document = CadApp.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            return null;
        }

        try
        {
            var resolved = HostApplicationServices.Current.FindFile(
                fileName,
                document.Database,
                FindFileHint.Default);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return Path.GetFullPath(resolved);
            }
        }
        catch
        {
            // 部分 CAD 版本不会通过 FindFile 返回 CTB，改走选项中的打印样式表搜索路径。
        }

        return null;
    }

    /** TryOpenStyleFile：用系统关联的打印样式表编辑器打开 CTB。 */
    private static bool TryOpenStyleFile(string stylePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = stylePath,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return TryRevealStyleFile(stylePath);
        }
    }

    /**
     * TryOpenCadPlotStyleManager：执行 CAD 的 STYLESMANAGER，打开当前配置的打印样式目录。
     * 无活动文档时改为直接打开选项里的第一个样式表搜索目录。
     */
    private static bool TryOpenCadPlotStyleManager()
    {
        var document = CadApp.DocumentManager.MdiActiveDocument;
        if (document != null)
        {
            try
            {
                document.SendStringToExecute("_.STYLESMANAGER ", true, false, false);
                return true;
            }
            catch
            {
            }
        }

        var firstDirectory = AcadPlotterInstaller.GetPlotStyleSearchDirectories().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstDirectory) || !Directory.Exists(firstDirectory))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = firstDirectory,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
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
