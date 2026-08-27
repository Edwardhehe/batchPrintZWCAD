using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZwcadBatchPlot;

/// <summary>加长图图名格式配置。共预留6种，当前实现配置1（分数）和配置2（小数）。</summary>
public enum LongPaperNameFormat
{
    /// <summary>配置1（分数）：A3+1/8、A2+3/4，分母为8的约分分数。</summary>
    Fraction = 0,
    /// <summary>配置2（小数）：A3+0.125、A2+0.75，精确到3位小数。</summary>
    Decimal = 1,
    Reserved2 = 2,
    Reserved3 = 3,
    Reserved4 = 4,
    Reserved5 = 5,
}

/// <summary>图框块批量打印的主排序依据。</summary>
public enum TitleBlockSortMode
{
    /// <summary>先按图号、图名排序；两者相同时再按图纸位置排序。</summary>
    DrawingNumber = 0,
    /// <summary>完全忽略图号和图名，只按图纸在当前图形中的位置排序。</summary>
    Spatial = 1
}

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
    public string LastPlotDevice { get; set; } = "";
    public string LastStyleSheet { get; set; } = "";
    public double PaperMatchToleranceMm { get; set; } = 1.0;
    /// <summary>命令快捷键：键为原始命令名（ZBP_*），值为用户设置的简化命令。</summary>
    public Dictionary<string, string> CommandAliases { get; set; } = new();
    /// <summary>
    /// 矩形框批打是否识别由 4 个独立直线实体或直线型开放 PL 首尾相连组成的矩形。
    /// 默认关闭，避免改变既有图纸的扫描结果。
    /// </summary>
    public bool RecognizeFourLineRectangleFrames { get; set; }
    /// <summary>
    /// 正式打印时是否把打印内容四边各裁 1mm 纸面，使图框外边框不再输出。
    /// 首次使用默认关闭；裁切不改纸张、比例和留白，也不修改 DWG。
    /// </summary>
    public bool HideFrameBoundaryWhenPlotting { get; set; } = false;
    public bool AddSequenceWhenPdfExists { get; set; } = false;
    /// <summary>记录批打印窗口上一次“合并 PDF”的勾选状态；首次使用默认为不勾选。</summary>
    public bool MergePdf { get; set; }
    /// <summary>合并 PDF 时，是否用每张图纸原始输出文件名创建一级书签。</summary>
    public bool UseFileNameAsPdfBookmark { get; set; }
    /// <summary>合并 PDF 时，是否按纸张物理尺寸分组；一批多种尺寸会输出多个 PDF。</summary>
    public bool MergePdfByPaperSize { get; set; }
    /// <summary>批量输出单张文件完成后，是否打开输出文件夹。</summary>
    public bool OpenOutputDirectoryAfterBatchPrint { get; set; } = true;
    /// <summary>PDF 合并成功后，是否用系统默认阅读器打开生成的合并文件。</summary>
    public bool OpenMergedPdfAfterMerge { get; set; } = true;
    /// <summary>
    /// 插件是否允许生成日志文件。默认关闭；统一控制打印、拆图、扫描警告和图框录入诊断日志。
    /// </summary>
    public bool GeneratePrintLog { get; set; }
    /// <summary>
    /// 使用插件自有 PDF/DWF 绘图仪时，是否把 TrueType 文字按图形轮廓输出。默认关闭，且不修改 DWG 文字实体。
    /// </summary>
    public bool ConvertTextToGeometryWhenPlotting { get; set; }
    /// <summary>
    /// 正式打印时是否输出对象透明度。默认开启，对应 CAD 打印对话框中的“打印透明度”。
    /// </summary>
    public bool PlotTransparency { get; set; } = true;
    public bool AddFileNameSequence { get; set; }
    public bool LeavePaperMargin { get; set; }
    public double PaperMarginMm { get; set; } = 1;
    /// <summary>图框块批量打印的主排序依据；矩形框批量打印不读取此设置。</summary>
    public TitleBlockSortMode TitleBlockBatchSortMode { get; set; } = TitleBlockSortMode.DrawingNumber;
    /// <summary>图框库批量打印空间排序方向：true=从左到右从上到下，false=从上到下从左到右。</summary>
    public bool SortOrderHorizontalFirst { get; set; }
    /// <summary>加长图图名命名格式：分数（配置1）或小数（配置2）等。</summary>
    public LongPaperNameFormat LongPaperNameFormat { get; set; } = LongPaperNameFormat.Fraction;
    /// <summary>
    /// 加长图长边吸附到最近 1/8 模数标准加长图的容差（毫米）。
    /// 同时影响实际打印纸张尺寸（PMP 注册）和文件名、图纸目录等输出显示名称。
    /// </summary>
    public double LongPaperSnapToleranceMm { get; set; } = 3.0;
    public string PdfFileNameSeparator { get; set; } = "_";
    public List<string> PdfFileNameFields { get; set; } = new() { "DrawingNumber", "Title" };
    /// <summary>
    /// 文件名规则：A=图号，B=版次，C=图名，D=日期，E=信息1，F=信息2，G=设计阶段，T=图幅，N=序号。
    /// 在占位字母前加反斜杠可输出字母本身，例如 \A 输出 A。
    /// </summary>
    public string PdfFileNamePattern { get; set; } = "";
    /// <summary>文件名中序号的补零位数；0 表示不补零。</summary>
    public int FileNameSequenceDigits { get; set; } = 2;
    /// <summary>是否根据本次清单最后一个序号自动推断补零位数。</summary>
    public bool AutoFileNameSequenceDigits { get; set; }
    /// <summary>文件名序号的起始值。</summary>
    public int FileNameSequenceStartNumber { get; set; } = 1;
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
    /// <summary>
    /// 用户自定义打印比例，语义与内置比例一致：图面尺寸 / 比例 = 纸张毫米尺寸。
    /// &gt;1 表示 1:N（如 143 表示 1:143）；&lt;1 表示放大比例（如 0.25 表示 4:1）。
    /// </summary>
    public List<double> CustomScales { get; set; } = new();
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
        // 旧版 Settings.json 不包含该字段时会得到枚举默认值 DrawingNumber；
        // 对异常整数再做一次兜底，避免损坏配置让排序行为不可预测。
        if (!Enum.IsDefined(typeof(TitleBlockSortMode), settings.TitleBlockBatchSortMode))
        {
            settings.TitleBlockBatchSortMode = TitleBlockSortMode.DrawingNumber;
        }

        if (settings.PaperMatchToleranceMm <= 0)
        {
            settings.PaperMatchToleranceMm = 1.0;
        }

        if (settings.LongPaperSnapToleranceMm <= 0)
        {
            settings.LongPaperSnapToleranceMm = 3.0;
        }

        // 命令快捷键：去掉未知命令、非法别名和被多个命令重复占用的别名。
        settings.CommandAliases = CommandAliasManager.NormalizeAliases(settings.CommandAliases);

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
        if (settings.PaperMarginMm == 0)
        {
            settings.PaperMarginMm = 1; // 0 无意义，默认1mm缩比例模式
        }

        settings.PdfFileNameSeparator = NormalizeFileNameSeparator(settings.PdfFileNameSeparator);
        if (settings.PdfFileNameFields == null || settings.PdfFileNameFields.Count == 0)
        {
            settings.PdfFileNameFields = new List<string> { "DrawingNumber", "Title" };
        }
        else
        {
            // 显式保序去重，不依赖 Enumerable.Distinct 的内部实现顺序
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<string>();
            foreach (var item in settings.PdfFileNameFields)
            {
                if (seen.Add(item))
                    deduped.Add(item);
            }
            settings.PdfFileNameFields = deduped;
        }

        // 图号为文件名必选字段
        if (!settings.PdfFileNameFields.Any(f => string.Equals(f, "DrawingNumber", StringComparison.OrdinalIgnoreCase)))
        {
            settings.PdfFileNameFields.Add("DrawingNumber");
        }

        // 旧版使用“字段列表 + 连接符”。第一次读取旧配置时转换为等价的规则字符串，
        // 保留用户原有的字段顺序和连接符；之后仅以规则字符串生成文件名。
        if (string.IsNullOrWhiteSpace(settings.PdfFileNamePattern))
        {
            settings.PdfFileNamePattern = BuildLegacyFileNamePattern(
                settings.PdfFileNameFields,
                settings.PdfFileNameSeparator);
        }

        if (settings.FileNameSequenceDigits < 0 || settings.FileNameSequenceDigits > 10)
        {
            settings.FileNameSequenceDigits = 2;
        }
        if (settings.FileNameSequenceStartNumber < 0)
        {
            settings.FileNameSequenceStartNumber = 1;
        }

        settings.CustomScales = NormalizeCustomScales(settings.CustomScales);

        return settings;
    }

    /// <summary>
    /// 自定义比例兜底校验：剔除非法值、按 1e-6 容差去重、剔除与内置比例重复的项并升序排列。
    /// </summary>
    private static List<double> NormalizeCustomScales(List<double>? scales)
    {
        var normalized = new List<double>();
        if (scales != null)
        {
            foreach (var scale in scales)
            {
                if (scale <= 0
                    || normalized.Any(x => Math.Abs(x - scale) < 1e-6)
                    || PaperSizeDetector.BuiltInScales.Any(x => Math.Abs(x - scale) < 1e-6))
                {
                    continue;
                }

                normalized.Add(scale);
            }
        }

        normalized.Sort();
        return normalized;
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

    private static string BuildLegacyFileNamePattern(IEnumerable<string> fieldKeys, string separator)
    {
        var tokens = new List<string>();
        foreach (var key in fieldKeys)
        {
            var token = key switch
            {
                "DrawingNumber" => "A",
                "Revision" => "B",
                "Title" => "C",
                "Date" => "D",
                "Info1" => "E",
                "Info2" => "F",
                "Phase" => "G",
                "PaperName" => "T",
                "Sequence" => "N",
                _ => ""
            };
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens.Count > 0 ? string.Join(separator, tokens) : "A_C";
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
