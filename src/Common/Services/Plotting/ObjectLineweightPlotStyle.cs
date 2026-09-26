using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.PlottingServices;
#endif

/**
 * @file ObjectLineweightPlotStyle.cs
 * @description “打印对象线宽”开关的实际落地：样式表其余设置照常生效，只有线宽改按对象/图层线宽出图。
 *
 * 主要功能：
 * - Resolve：勾选时为所选 CTB/STB 生成（或复用）临时副本 <原名>__objlw.ctb，副本中所有样式线宽 = 使用对象线宽
 * - Apply / ApplyFlags：写入 styleSheet，并显式设置 PlotPlotStyles / PrintLineweights
 * - LogEffective：把实际生效的 styleSheet 与两个开关写入打印日志
 *
 * 核心规则：
 * - 副本写在 CAD 解析到原样式表的同一目录（该目录不在打印样式搜索路径中时改写到第一个搜索目录）
 * - 原样式表只读不写；源文件大小/修改时间变化或副本缺失时才重新生成，内容相同则不重写
 * - 生成失败或 CAD 不接受副本时记录原因并回退原样式表（仍设置开关），不影响出图
 * - 未勾选时完全按原样式表出图：PrintLineweights = false，选了样式时 PlotPlotStyles = true
 */

namespace ZwcadBatchPlot;

/** 一次出图任务的打印样式决策结果。 */
internal sealed class PlotStyleChoice
{
    /** 用户选择的原样式表文件名（空表示无）。 */
    public string RequestedStyle { get; set; } = "";

    /** 实际交给 CAD 的样式表文件名（勾选“打印对象线宽”时通常为 __objlw 副本）。 */
    public string EffectiveStyle { get; set; } = "";

    /** 常规设置“打印对象线宽”。 */
    public bool PlotObjectLineweights { get; set; }

    /** 副本生成/回退说明，写入日志。 */
    public string Note { get; set; } = "";

    public bool HasStyle => !string.IsNullOrWhiteSpace(RequestedStyle);

    public bool UsesObjectLineweightCopy =>
        HasStyle && !string.Equals(EffectiveStyle, RequestedStyle, StringComparison.OrdinalIgnoreCase);
}

internal static class ObjectLineweightPlotStyle
{
    private sealed class CopyState
    {
        public long SourceLength { get; set; }
        public DateTime SourceWriteTimeUtc { get; set; }
        public string CopyPath { get; set; } = "";
    }

    private static readonly object Sync = new();
    private static readonly Dictionary<string, CopyState> CopyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> SourcePathCache = new(StringComparer.OrdinalIgnoreCase);

    /**
     * Resolve：决定本次出图使用的样式表；勾选“打印对象线宽”时准备对象线宽副本。
     * 在创建 PlotSettings 前调用一次（每张图一次），失败时回退原样式表并记录原因。
     */
    public static PlotStyleChoice Resolve(string? styleSheet, bool plotObjectLineweights)
    {
        var requested = PlotStyleManager.NormalizeStyleName(styleSheet);
        // 下拉框已隐藏副本；万一作业里残留副本名，仍按原样式表处理，避免对副本再生成副本。
        requested = PlotStyleLineweightConverter.GetSourceFileName(requested);

        var choice = new PlotStyleChoice
        {
            RequestedStyle = requested,
            EffectiveStyle = requested,
            PlotObjectLineweights = plotObjectLineweights
        };

        if (!plotObjectLineweights || !choice.HasStyle)
        {
            return choice;
        }

        if (!PlotStyleLineweightConverter.IsSupportedStyleFile(requested))
        {
            choice.Note = $"样式表“{requested}”不是 CTB/STB 文件，无法生成对象线宽副本，仅设置 PrintLineweights。";
            return choice;
        }

        try
        {
            choice.EffectiveStyle = EnsureObjectLineweightCopy(requested, out var note);
            choice.Note = note;
        }
        catch (Exception ex)
        {
            choice.EffectiveStyle = requested;
            choice.Note = $"生成对象线宽副本失败，改用原样式表“{requested}”（样式表线宽仍会生效）：{ex.Message}";
        }

        return choice;
    }

    /**
     * Apply：写入样式表并显式设置 PlotPlotStyles / PrintLineweights。
     * CAD 不接受副本名时刷新样式列表重试一次，仍失败则回退原样式表。
     */
    public static void Apply(PlotSettingsValidator validator, PlotSettings settings, PlotStyleChoice choice)
    {
        if (choice.HasStyle)
        {
            if (choice.UsesObjectLineweightCopy)
            {
                if (!TrySetStyleSheet(validator, settings, choice.EffectiveStyle, out var firstError))
                {
                    RefreshStyleList();
                    if (!TrySetStyleSheet(validator, settings, choice.EffectiveStyle, out var secondError))
                    {
                        choice.Note = AppendNote(
                            choice.Note,
                            $"CAD 不接受对象线宽副本“{choice.EffectiveStyle}”（{secondError ?? firstError}），改用原样式表“{choice.RequestedStyle}”。");
                        choice.EffectiveStyle = choice.RequestedStyle;
                        validator.SetCurrentStyleSheet(settings, choice.RequestedStyle);
                    }
                }
            }
            else
            {
                validator.SetCurrentStyleSheet(settings, choice.RequestedStyle);
            }
        }

        ApplyFlags(settings, choice);
    }

    /** ApplyFlags：显式写入 PlotPlotStyles（有样式时）与 PrintLineweights；CopyFrom(layout) 带入的旧值一律覆盖。 */
    public static void ApplyFlags(PlotSettings settings, PlotStyleChoice choice)
    {
        if (choice.HasStyle)
        {
            settings.PlotPlotStyles = true;
        }

        settings.PrintLineweights = choice.PlotObjectLineweights;
    }

    /** LogEffective：记录实际生效的 styleSheet、PlotPlotStyles、PrintLineweights（受“生成打印日志”开关控制）。 */
    public static void LogEffective(PlotSettings settings, PlotStyleChoice choice, PlotJob job, string mode)
    {
        try
        {
            if (!BatchPlotLogger.IsEnabled)
            {
                return;
            }

            string actualStyle;
            bool plotPlotStyles;
            bool printLineweights;
            try
            {
                actualStyle = settings.CurrentStyleSheet ?? "";
                plotPlotStyles = settings.PlotPlotStyles;
                printLineweights = settings.PrintLineweights;
            }
            catch (Exception ex)
            {
                BatchPlotLogger.AddPending("WARN", $"读取打印样式设置失败：{ex.Message}");
                return;
            }

            var message =
                $"{mode}打印样式 {job.DrawingNumber}_{job.Title}：所选样式={(choice.HasStyle ? choice.RequestedStyle : "(无)")}；"
                + $"实际 styleSheet={(string.IsNullOrWhiteSpace(actualStyle) ? "(无)" : actualStyle)}；"
                + $"PlotPlotStyles={plotPlotStyles}；PrintLineweights={printLineweights}；"
                + $"打印对象线宽={(choice.PlotObjectLineweights ? "开" : "关")}";
            if (!string.IsNullOrWhiteSpace(choice.Note))
            {
                message += "；" + choice.Note;
            }

            var level = choice.PlotObjectLineweights && choice.HasStyle && !choice.UsesObjectLineweightCopy ? "WARN" : "INFO";
            BatchPlotLogger.AddPending(level, message);
        }
        catch
        {
            // 日志失败不得影响出图。
        }
    }

    /**
     * EnsureObjectLineweightCopy：生成或复用对象线宽副本，返回副本文件名（CAD 按文件名在样式搜索路径中解析）。
     */
    private static string EnsureObjectLineweightCopy(string requested, out string note)
    {
        var sourcePath = ResolveSourcePath(requested)
            ?? throw new FileNotFoundException($"未在 CAD 打印样式搜索路径中找到“{requested}”。");
        var sourceInfo = new FileInfo(sourcePath);
        var copyFileName = PlotStyleLineweightConverter.GetCopyFileName(sourceInfo.Name);

        lock (Sync)
        {
            if (CopyCache.TryGetValue(sourcePath, out var state)
                && state.SourceLength == sourceInfo.Length
                && state.SourceWriteTimeUtc == sourceInfo.LastWriteTimeUtc
                && File.Exists(state.CopyPath))
            {
                note = $"复用对象线宽副本 {state.CopyPath}";
                return Path.GetFileName(state.CopyPath);
            }

            var sourceBytes = ReadAllBytesShared(sourcePath);
            var converted = PlotStyleLineweightConverter.Convert(sourceBytes, out var changed, out var total);

            var errors = new List<string>();
            foreach (var directory in GetCopyDirectories(sourcePath))
            {
                var copyPath = Path.Combine(directory, copyFileName);
                if (string.Equals(Path.GetFullPath(copyPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 保险：绝不覆盖原样式表。
                }

                try
                {
                    var upToDate = File.Exists(copyPath) && BytesEqual(ReadAllBytesShared(copyPath), converted);
                    if (!upToDate)
                    {
                        WriteAllBytesAtomically(copyPath, converted);
                    }

                    CopyCache[sourcePath] = new CopyState
                    {
                        SourceLength = sourceInfo.Length,
                        SourceWriteTimeUtc = sourceInfo.LastWriteTimeUtc,
                        CopyPath = copyPath
                    };
                    RefreshStyleList();
                    note = upToDate
                        ? $"对象线宽副本已是最新 {copyPath}"
                        : $"已生成对象线宽副本 {copyPath}（{changed}/{total} 个样式线宽改为使用对象线宽，源={sourcePath}）";
                    return copyFileName;
                }
                catch (Exception ex)
                {
                    errors.Add($"{copyPath}: {ex.Message}");
                }
            }

            throw new IOException("无法写入对象线宽副本：" + string.Join("；", errors));
        }
    }

    private static string? ResolveSourcePath(string requested)
    {
        lock (Sync)
        {
            if (SourcePathCache.TryGetValue(requested, out var cached) && File.Exists(cached))
            {
                return cached;
            }
        }

        var resolved = PlotStyleManager.ResolveStylePath(requested);
        if (string.IsNullOrWhiteSpace(resolved) || !File.Exists(resolved))
        {
            return null;
        }

        lock (Sync)
        {
            SourcePathCache[requested] = resolved!;
        }

        return resolved;
    }

    /**
     * GetCopyDirectories：副本候选目录。原样式表所在目录在搜索路径中（或取不到搜索路径）时优先写在同目录；
     * 否则先写搜索路径，保证 CAD 能按文件名找到副本。
     */
    private static IEnumerable<string> GetCopyDirectories(string sourcePath)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? "";
        IReadOnlyList<string> searchDirectories;
        try
        {
            searchDirectories = AcadPlotterInstaller.GetPlotStyleSearchDirectories();
        }
        catch
        {
            searchDirectories = Array.Empty<string>();
        }

        var ordered = new List<string>();
        var sourceInSearchPath = searchDirectories.Count == 0
                                 || searchDirectories.Any(directory => SameDirectory(directory, sourceDirectory));
        if (sourceInSearchPath && !string.IsNullOrEmpty(sourceDirectory))
        {
            ordered.Add(sourceDirectory);
        }

        foreach (var directory in searchDirectories)
        {
            if (!ordered.Any(existing => SameDirectory(existing, directory)))
            {
                ordered.Add(directory);
            }
        }

        if (ordered.Count == 0 && !string.IsNullOrEmpty(sourceDirectory))
        {
            ordered.Add(sourceDirectory);
        }

        return ordered;
    }

    private static bool SameDirectory(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\', '/'),
                Path.GetFullPath(right).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetStyleSheet(PlotSettingsValidator validator, PlotSettings settings, string styleSheet, out string? error)
    {
        try
        {
            validator.SetCurrentStyleSheet(settings, styleSheet);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void RefreshStyleList()
    {
        try
        {
            PlotConfigManager.RefreshList(RefreshCode.RefreshStyleList);
        }
        catch
        {
            // 刷新失败时由 SetCurrentStyleSheet 的回退逻辑兜底。
        }
    }

    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream((int)Math.Max(0, Math.Min(stream.Length, int.MaxValue)));
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void WriteAllBytesAtomically(string path, byte[] bytes)
    {
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            if (File.Exists(path))
            {
                File.Copy(tempPath, path, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // 临时文件删除失败不影响结果（扩展名不是 .ctb/.stb，不会出现在样式列表中）。
            }
        }
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string AppendNote(string existing, string addition)
        => string.IsNullOrWhiteSpace(existing) ? addition : existing + "；" + addition;
}