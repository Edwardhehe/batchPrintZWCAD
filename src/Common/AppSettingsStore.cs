using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

public sealed class DirectoryColumnSetting
{
    public string Key { get; set; } = "";
    public string Header { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Centered { get; set; } = true;
    public double Width { get; set; }

    public DirectoryColumnSetting Clone()
    {
        return new DirectoryColumnSetting
        {
            Key = Key,
            Header = Header,
            Enabled = Enabled,
            Centered = Centered,
            Width = Width
        };
    }
}

public sealed class AppSettings
{
    public string LastOutputDirectory { get; set; } = "";
    public string LastPlotDevice { get; set; } = "";
    public string LastStyleSheet { get; set; } = "";
    public bool RememberLastOutputDirectory { get; set; } = true;
    public string DefaultOutputSubfolder { get; set; } = "PDF";
    public bool AutoScanCurrentDrawing { get; set; }
    public double PaperMatchToleranceMm { get; set; } = 3;
    public bool AllowStandardPaperNameFallback { get; set; } = true;
    public bool ShowPlotProgress { get; set; } = true;
    public bool AddSequenceWhenPdfExists { get; set; } = false;
    public bool MergePdf { get; set; }
    public bool AddFileNameSequence { get; set; }
    public bool LeavePaperMargin { get; set; }
    public double PaperMarginMm { get; set; } = 1;
    public string PdfFileNameSeparator { get; set; } = "_";
    public List<string> PdfFileNameFields { get; set; } = new() { "DrawingNumber", "Title" };
    public bool OpenExternalDwgForPlot { get; set; } = true;
    public double DirectoryIndexWidth { get; set; } = 900;
    public double DirectoryNumberWidth { get; set; } = 3200;
    public double DirectoryTitleWidth { get; set; } = 5200;
    public double DirectoryPaperWidth { get; set; } = 1200;
    public double DirectoryRemarkWidth { get; set; } = 1400;
    public double DirectoryRowHeight { get; set; } = 650;
    public double DirectoryTextHeightRatio { get; set; } = 0.42;
    public string DirectoryTextStyleName { get; set; } = "宋体";
    public int DirectoryColorIndex { get; set; } = 7;
    public int DirectorySettingsVersion { get; set; }
    public double DirectoryTextHeight { get; set; } = 450;
    public double DirectoryTextWidthFactor { get; set; } = 0.7;
    public string DirectoryLayerName { get; set; } = "0";
    public bool DirectoryDrawHeader { get; set; } = true;
    public bool DirectoryDrawGridLines { get; set; } = true;
    public List<DirectoryColumnSetting> DirectoryColumns { get; set; } = new();
}

public static class AppSettingsStore
{
    // 仅允许 Windows 文件名可安全使用的连接符；明确排除 \\ / : * ? " < > | 等非法字符。
    private static readonly HashSet<string> AllowedFileNameSeparators = new(StringComparer.Ordinal)
    {
        "_", " ", "+", "-", ".", "~", "=", ""
    };

    public static string Path =>
        System.IO.Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return LoadFrom(Path);
            }

            var backupPath = Path + ".bak";
            return File.Exists(backupPath) ? LoadFrom(backupPath) : Normalize(new AppSettings());
        }
        catch
        {
            var backupPath = Path + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    return LoadFrom(backupPath);
                }
                catch
                {
                }
            }

            return Normalize(new AppSettings());
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        var json = JsonConvert.SerializeObject(Normalize(settings), Formatting.Indented);
        WriteAtomically(Path, json);
    }

    public static AppSettings Default()
    {
        return Normalize(new AppSettings());
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DefaultOutputSubfolder))
        {
            settings.DefaultOutputSubfolder = "PDF";
        }

        if (settings.PaperMatchToleranceMm <= 0)
        {
            settings.PaperMatchToleranceMm = 3;
        }

        if (settings.DirectoryIndexWidth <= 0)
        {
            settings.DirectoryIndexWidth = 900;
        }

        if (settings.DirectoryNumberWidth <= 0)
        {
            settings.DirectoryNumberWidth = 3200;
        }

        if (settings.DirectoryTitleWidth <= 0)
        {
            settings.DirectoryTitleWidth = 5200;
        }

        if (settings.DirectoryPaperWidth <= 0)
        {
            settings.DirectoryPaperWidth = 1200;
        }

        if (settings.DirectoryRemarkWidth <= 0)
        {
            settings.DirectoryRemarkWidth = 1400;
        }

        if (settings.DirectoryRowHeight <= 0)
        {
            settings.DirectoryRowHeight = 650;
        }

        if (settings.DirectoryTextHeightRatio <= 0 || settings.DirectoryTextHeightRatio > 0.9)
        {
            settings.DirectoryTextHeightRatio = 0.42;
        }

        settings.DirectoryTextStyleName ??= "";
        if (settings.DirectoryColorIndex < 0 || settings.DirectoryColorIndex > 256)
        {
            settings.DirectoryColorIndex = 7;
        }

        // 目录设置第 2 版把早期默认字高 300 调整为 450；只迁移旧版默认值，用户后续主动输入 300 仍会保留。
        if (settings.DirectorySettingsVersion < 2)
        {
            if (settings.DirectoryTextHeight <= 0 || Math.Abs(settings.DirectoryTextHeight - 300) < 1e-6)
            {
                settings.DirectoryTextHeight = 450;
            }
            settings.DirectorySettingsVersion = 2;
        }
        else if (settings.DirectoryTextHeight <= 0)
        {
            settings.DirectoryTextHeight = Math.Max(1, settings.DirectoryRowHeight * settings.DirectoryTextHeightRatio);
        }

        if (settings.DirectoryTextWidthFactor <= 0 || settings.DirectoryTextWidthFactor > 10)
        {
            settings.DirectoryTextWidthFactor = 0.7;
        }

        settings.DirectoryLayerName = string.IsNullOrWhiteSpace(settings.DirectoryLayerName)
            ? "0"
            : settings.DirectoryLayerName.Trim();

        settings.DirectoryColumns = NormalizeDirectoryColumns(settings);
        if (settings.DirectorySettingsVersion < 3)
        {
            ApplyDirectoryVersion3Defaults(settings);
            settings.DirectorySettingsVersion = 3;
        }
        SyncLegacyDirectoryWidths(settings);
        if (settings.PaperMarginMm <= 0)
        {
            settings.PaperMarginMm = 1;
        }

        settings.PdfFileNameSeparator = NormalizeFileNameSeparator(settings.PdfFileNameSeparator);
        if (settings.PdfFileNameFields == null || settings.PdfFileNameFields.Count == 0)
        {
            settings.PdfFileNameFields = new List<string> { "DrawingNumber", "Title" };
        }
        else
        {
            // 去重，防止旧版 UI 产生的重复项
            settings.PdfFileNameFields = settings.PdfFileNameFields
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return settings;
    }

    private static List<DirectoryColumnSetting> NormalizeDirectoryColumns(AppSettings settings)
    {
        // 列定义必须与 PlotJob 中真实保存的图框识别字段一一对应，避免界面出现无法生成内容的“展示列”。
        var defaults = CreateDefaultDirectoryColumns(settings);
        if (settings.DirectoryColumns == null || settings.DirectoryColumns.Count == 0)
        {
            return defaults;
        }

        var defaultByKey = defaults.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        var normalized = new List<DirectoryColumnSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in settings.DirectoryColumns)
        {
            if (column == null || !defaultByKey.TryGetValue(column.Key ?? "", out var fallback) || !seen.Add(fallback.Key))
            {
                continue;
            }

            normalized.Add(new DirectoryColumnSetting
            {
                Key = fallback.Key,
                Header = string.IsNullOrWhiteSpace(column.Header) ? fallback.Header : column.Header.Trim(),
                Enabled = column.Enabled,
                Centered = column.Centered,
                Width = column.Width > 0 ? column.Width : fallback.Width
            });
        }

        // 新版本新增识别字段时只补到列表末尾且默认停用，不改变用户已经保存的目录版式。
        foreach (var fallback in defaults.Where(x => !seen.Contains(x.Key)))
        {
            var added = fallback.Clone();
            added.Enabled = false;
            normalized.Add(added);
        }

        return normalized;
    }

    private static List<DirectoryColumnSetting> CreateDefaultDirectoryColumns(AppSettings settings)
    {
        return new List<DirectoryColumnSetting>
        {
            new() { Key = "Sequence", Header = "序号", Enabled = true, Centered = true, Width = settings.DirectoryIndexWidth },
            new() { Key = "DrawingNumber", Header = "图号", Enabled = true, Centered = true, Width = settings.DirectoryNumberWidth },
            new() { Key = "Title", Header = "图名", Enabled = true, Centered = true, Width = settings.DirectoryTitleWidth },
            new() { Key = "PaperName", Header = "图幅", Enabled = true, Centered = true, Width = settings.DirectoryPaperWidth },
            new() { Key = "Revision", Header = "版次", Enabled = false, Centered = true, Width = 2800 },
            new() { Key = "Date", Header = "日期", Enabled = false, Centered = true, Width = 2000 },
            new() { Key = "Phase", Header = "设计阶段", Enabled = false, Centered = true, Width = 2400 },
            new() { Key = "Info1", Header = "信息1", Enabled = false, Centered = true, Width = 2000 },
            new() { Key = "Info2", Header = "信息2", Enabled = false, Centered = true, Width = 2000 }
        };
    }

    private static void ApplyDirectoryVersion3Defaults(AppSettings settings)
    {
        // 第 3 版统一初始顺序和勾选状态；完成一次迁移后，后续由用户保存的个性化顺序不再被覆盖。
        var order = new[] { "Sequence", "DrawingNumber", "Title", "PaperName", "Revision", "Date", "Phase", "Info1", "Info2" };
        var enabled = new HashSet<string>(
            new[] { "Sequence", "DrawingNumber", "Title", "PaperName" },
            StringComparer.OrdinalIgnoreCase);
        var byKey = settings.DirectoryColumns.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
        settings.DirectoryColumns = order
            .Where(byKey.ContainsKey)
            .Select(key =>
            {
                var column = byKey[key];
                column.Enabled = enabled.Contains(key);
                column.Centered = true;
                return column;
            })
            .ToList();
        settings.DirectoryDrawHeader = true;
        settings.DirectoryDrawGridLines = true;
        settings.DirectoryLayerName = "0";
        settings.DirectoryTextStyleName = "宋体";
    }

    private static void SyncLegacyDirectoryWidths(AppSettings settings)
    {
        // 同步旧字段，确保旧版调用路径或用户降级加载配置时仍能得到前五列的有效尺寸。
        settings.DirectoryIndexWidth = FindDirectoryWidth(settings, "Sequence", settings.DirectoryIndexWidth);
        settings.DirectoryNumberWidth = FindDirectoryWidth(settings, "DrawingNumber", settings.DirectoryNumberWidth);
        settings.DirectoryTitleWidth = FindDirectoryWidth(settings, "Title", settings.DirectoryTitleWidth);
        settings.DirectoryPaperWidth = FindDirectoryWidth(settings, "PaperName", settings.DirectoryPaperWidth);
        settings.DirectoryRemarkWidth = Math.Max(1, settings.DirectoryRemarkWidth);
    }

    private static double FindDirectoryWidth(AppSettings settings, string key, double fallback)
    {
        return settings.DirectoryColumns.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))?.Width
            ?? Math.Max(1, fallback);
    }

    public static string NormalizeFileNameSeparator(string? separator)
    {
        return AllowedFileNameSeparators.Contains(separator ?? "_") ? separator ?? "_" : "_";
    }

    private static void WriteAtomically(string path, string contents)
    {
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";
        File.WriteAllText(tempPath, contents);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, backupPath, true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static AppSettings LoadFrom(string path)
    {
        var json = File.ReadAllText(path);
        return Normalize(JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings());
    }
}
