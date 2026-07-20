using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ZwcadBatchPlot;

public sealed class PdfMergeInput
{
    public PdfMergeInput(
        string filePath,
        string bookmarkTitle,
        string paperName = "",
        double paperWidthMm = 0,
        double paperHeightMm = 0)
    {
        FilePath = filePath;
        BookmarkTitle = bookmarkTitle;
        PaperName = paperName;
        PaperWidthMm = paperWidthMm;
        PaperHeightMm = paperHeightMm;
    }

    public string FilePath { get; }
    public string BookmarkTitle { get; }
    public string PaperName { get; }
    public double PaperWidthMm { get; }
    public double PaperHeightMm { get; }
}

public sealed class PdfMergePlan
{
    public PdfMergePlan(string outputPath, IReadOnlyList<PdfMergeInput> inputs)
    {
        OutputPath = outputPath;
        Inputs = inputs;
    }

    public string OutputPath { get; }
    public IReadOnlyList<PdfMergeInput> Inputs { get; }
}

public static class PdfDocumentService
{
    public static void Merge(IReadOnlyList<string> inputFiles, string outputPath)
    {
        var inputs = inputFiles?
            .Select(file => new PdfMergeInput(file, Path.GetFileNameWithoutExtension(file)))
            .ToList() ?? new List<PdfMergeInput>();
        Merge(inputs, outputPath, addFileNameBookmarks: true);
    }

    public static void Merge(
        IReadOnlyList<PdfMergeInput> inputFiles,
        string outputPath,
        bool addFileNameBookmarks)
    {
        if (inputFiles == null || inputFiles.Count == 0)
        {
            throw new InvalidOperationException("没有可合并的 PDF 文件。");
        }

        foreach (var input in inputFiles)
        {
            var file = input.FilePath;
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                throw new FileNotFoundException("待合并的 PDF 文件不存在。", file);
            }
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new InvalidOperationException("合并 PDF 输出路径无效: " + outputPath);
        Directory.CreateDirectory(directory);

        var temporaryOutput = fullOutputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.pdf";
        try
        {
            using (var output = new PdfDocument())
            {
                output.PageLayout = PdfPageLayout.SinglePage;
                if (addFileNameBookmarks)
                {
                    // 明确要求阅读器打开书签面板，否则已写入的书签在部分阅读器中默认不可见。
                    output.PageMode = PdfPageMode.UseOutlines;
                }

                foreach (var mergeInput in inputFiles)
                {
                    var file = mergeInput.FilePath;
                    using var input = OpenImportWithRetry(file);
                    PdfPage? firstPage = null;
                    foreach (var page in input.Pages)
                    {
                        var newPage = output.AddPage(page);
                        firstPage ??= newPage;
                    }

                    if (addFileNameBookmarks && firstPage != null)
                    {
                        var bookmarkTitle = string.IsNullOrWhiteSpace(mergeInput.BookmarkTitle)
                            ? Path.GetFileNameWithoutExtension(file)
                            : mergeInput.BookmarkTitle.Trim();
                        // 书签属于用户明确选择的输出内容；创建失败时应终止合并，不能静默生成缺少书签的 PDF。
                        output.Outlines.Add(
                            bookmarkTitle,
                            firstPage,
                            true,
                            PdfOutlineStyle.Bold);
                    }
                }

                if (output.PageCount == 0)
                {
                    throw new InvalidDataException("待合并 PDF 中没有有效页面。");
                }

                output.Save(temporaryOutput);
            }

            Validate(temporaryOutput);
            if (File.Exists(fullOutputPath))
            {
                File.Delete(fullOutputPath);
            }

            File.Move(temporaryOutput, fullOutputPath);
        }
        finally
        {
            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }
        }
    }

    public static IReadOnlyList<PdfMergePlan> PlanMerges(
        IReadOnlyList<PdfMergeInput> inputs,
        string requestedOutputPath,
        bool groupByPaperSize)
    {
        if (inputs == null || inputs.Count == 0)
        {
            throw new InvalidOperationException("没有可合并的 PDF 文件。");
        }

        if (!groupByPaperSize)
        {
            return new[] { new PdfMergePlan(requestedOutputPath, inputs.ToList()) };
        }

        // 宽高按短边/长边归一化，因此同一张纸横放和竖放仍属于同一尺寸组。
        // 以 0.1 mm 为粒度消除 CAD 浮点尾差，同时不会把实际不同的标准/加长纸误合并。
        var groups = inputs
            .GroupBy(input => PaperSizeKey.Create(input.PaperWidthMm, input.PaperHeightMm))
            .ToList();
        if (groups.Count == 1)
        {
            return new[] { new PdfMergePlan(requestedOutputPath, groups[0].ToList()) };
        }

        var plans = new List<PdfMergePlan>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var first = group.First();
            var suffix = BuildPaperSizeSuffix(first, group.Key);
            var outputPath = AppendFileNameSuffix(requestedOutputPath, suffix);
            var candidate = outputPath;
            var sequence = 2;
            while (!usedPaths.Add(Path.GetFullPath(candidate)))
            {
                candidate = AppendFileNameSuffix(outputPath, sequence.ToString());
                sequence++;
            }

            plans.Add(new PdfMergePlan(candidate, group.ToList()));
        }

        return plans;
    }

    private static string BuildPaperSizeSuffix(PdfMergeInput input, PaperSizeKey size)
    {
        // 先做加长图幅分数规范化（A3+1/4 → A3+0.25），再做文件名清洗，否则 / 会被过滤掉。
        var paperName = SanitizeFileNamePart(FileNameSanitizer.NormalizeLongPaperFraction(input.PaperName));
        if (!string.IsNullOrWhiteSpace(paperName))
        {
            // 文件名只保留图幅名（如 A2、A1+0.25），不附带具体尺寸数值。
            return paperName;
        }

        return size.IsKnown
            ? $"{size.LongSideMm:0.0}x{size.ShortSideMm:0.0}mm"
            : "未知尺寸";
    }

    private static string AppendFileNameSuffix(string path, string suffix)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var extension = Path.GetExtension(path);
        var stem = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, stem + "_" + suffix + extension);
    }

    private static string SanitizeFileNamePart(string value)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        return new string((value ?? "").Trim().Where(character => !invalid.Contains(character)).ToArray());
    }

    private readonly struct PaperSizeKey : IEquatable<PaperSizeKey>
    {
        private PaperSizeKey(long shortSideTenths, long longSideTenths, bool isKnown)
        {
            ShortSideTenths = shortSideTenths;
            LongSideTenths = longSideTenths;
            IsKnown = isKnown;
        }

        private long ShortSideTenths { get; }
        private long LongSideTenths { get; }
        public bool IsKnown { get; }
        public double ShortSideMm => ShortSideTenths / 10d;
        public double LongSideMm => LongSideTenths / 10d;

        public static PaperSizeKey Create(double widthMm, double heightMm)
        {
            if (widthMm <= 0 || heightMm <= 0 || double.IsNaN(widthMm) || double.IsNaN(heightMm))
            {
                return new PaperSizeKey(0, 0, false);
            }

            var shortSide = Math.Min(widthMm, heightMm);
            var longSide = Math.Max(widthMm, heightMm);
            return new PaperSizeKey(
                (long)Math.Round(shortSide * 10d, MidpointRounding.AwayFromZero),
                (long)Math.Round(longSide * 10d, MidpointRounding.AwayFromZero),
                true);
        }

        public bool Equals(PaperSizeKey other)
        {
            return ShortSideTenths == other.ShortSideTenths
                && LongSideTenths == other.LongSideTenths
                && IsKnown == other.IsKnown;
        }

        public override bool Equals(object? obj) => obj is PaperSizeKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = ShortSideTenths.GetHashCode();
                hash = (hash * 397) ^ LongSideTenths.GetHashCode();
                return (hash * 397) ^ IsKnown.GetHashCode();
            }
        }
    }

    private static PdfDocument OpenImportWithRetry(string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                }

                return PdfReader.Open(path, PdfDocumentOpenMode.Import);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PdfReaderException)
            {
                lastError = ex;
                Thread.Sleep(200);
            }
        }

        throw new IOException("PDF 文件尚未就绪，无法合并: " + path, lastError);
    }

    public static void Validate(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new IOException("PDF 文件不存在或为空: " + path);
        }

        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0)
        {
            throw new InvalidDataException("PDF 没有有效页面: " + path);
        }
    }
}
