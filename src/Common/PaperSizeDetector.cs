using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZwcadBatchPlot;

public static class PaperSizeDetector
{
    private const double StandardPaperTolerance = 0.04d;
    private const double DefaultLongPaperShortSideTolerance = 0.04d;
    private const double LongPaperSnapTolerance = 0.08d;
    private const int LongPaperIncrementDenominator = 8;
    private const int MinimumLongPaperUnits = LongPaperIncrementDenominator + 1;

    public sealed class DetectionOptions
    {
        public double LongPaperShortSideTolerance { get; set; } = DefaultLongPaperShortSideTolerance;
        /// <summary>任意加长图短边的绝对匹配容差（毫米）；设置后优先于相对容差。</summary>
        public double? LongPaperShortSideToleranceMm { get; set; }
        /// <summary>长边只要求超过标准长边，按实测尺寸返回，不再吸附到 1/8 模数。</summary>
        public bool AllowArbitraryLongSide { get; set; }
        /// <summary>同一几何尺寸存在多个比例候选时的首选比例；布局空间使用 1:1。</summary>
        public double? PreferredScaleValue { get; set; }
        /// <summary>多个优先比例，按数组顺序排序；矩形框批打用于优先 1:100 和 1:1。</summary>
        public IReadOnlyList<double>? PreferredScaleValues { get; set; }
        /// <summary>图框库固定纸张的物理宽度（毫米）；大于 0 时，纸张物理尺寸与之在容差内一致的候选优先。</summary>
        public double PreferredPaperWidthMm { get; set; }
        /// <summary>图框库固定纸张的物理高度（毫米）。</summary>
        public double PreferredPaperHeightMm { get; set; }
        /// <summary>
        /// 矩形框专用：只用短边匹配标准幅面；长边先按标准尺寸或 1/8 加长模数吸附，
        /// 超过容差时保留实测长边并转为任意动态纸张。
        /// </summary>
        public bool UseRectangleShortSideMatching { get; set; }
    }

    public static readonly DetectionOptions DefaultDetectionOptions = new();

    private sealed class PaperCandidate
    {
        public StandardPaper Paper { get; set; } = null!;
        public double Scale { get; set; }
        public double Score { get; set; }
        public bool IsLong { get; set; }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public string Reason { get; set; } = "";
        public bool RequiresCustomPaper { get; set; }
    }

    private sealed class StandardPaper
    {
        public StandardPaper(string name, double shortSide, double longSide)
        {
            Name = name;
            ShortSide = shortSide;
            LongSide = longSide;
        }

        public string Name { get; }
        public double ShortSide { get; }
        public double LongSide { get; }
    }

    private static readonly StandardPaper[] Standards =
    {
        new("A0", 841, 1189),
        new("A1", 594, 841),
        new("A2", 420, 594),
        new("A3", 297, 420),
        new("A4", 210, 297)
    };

    private static readonly double[] CommonScales =
    {
        0.01d, 0.02d, 0.025d, 0.04d, 0.05d, 0.1d, 0.2d, 0.25d, 0.4d, 0.5d,
        1d, 2d, 5d, 10d, 20d, 25d, 30d, 40d, 50d, 60d, 75d, 80d, 90d, 100d,
        120d, 125d, 150d, 200d, 250d, 300d, 400d, 500d, 600d, 1000d
    };

    private static readonly int[] IntegerScales = { 1, 2, 4, 5, 8, 10, 20, 25, 50, 100, 200, 500, 1000 };

    /// <summary>
    /// 根据图纸短边尺寸推测最可能的整数打印比例。
    /// 尝试每个常规整数比例，取使纸张短边落入 100-900mm 范围且最接近 A4~A0 短边的。
    /// 推测失败返回 0。
    /// </summary>
    public static int GuessScale(double drawingWidth, double drawingHeight)
    {
        var shortSide = Math.Min(drawingWidth, drawingHeight);
        if (shortSide <= 1e-6) return 0;

        var standardShorts = new[] { 210d, 297d, 420d, 594d, 841d }; // A4 A3 A2 A1 A0 短边
        var bestScale = 0;
        var bestDistance = double.MaxValue;

        foreach (var scale in IntegerScales)
        {
            var paperShort = shortSide / scale;
            if (paperShort < 100 || paperShort > 900) continue; // 纸张太极端

            // 找最接近的标准短边
            var nearest = standardShorts.OrderBy(s => Math.Abs(s - paperShort)).First();
            var distance = Math.Abs(nearest - paperShort);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestScale = scale;
            }
        }

        return bestScale;
    }

    /// <summary>
    /// 根据图纸尺寸和整数比例计算自定义纸张尺寸（mm）。
    /// 返回 (paperWidth, paperHeight, scale)。
    /// </summary>
    public static (double PaperWidth, double PaperHeight, double Scale) CalculateCustomPaper(
        double drawingWidth, double drawingHeight, int scale)
    {
        var paperWidth = drawingWidth / scale;
        var paperHeight = drawingHeight / scale;
        return (paperWidth, paperHeight, scale);
    }

    public static PaperDetection Detect(double width, double height)
    {
        return Detect(width, height, DefaultDetectionOptions);
    }

    public static PaperDetection Detect(double width, double height, DetectionOptions options)
    {
        var candidates = GetCandidateDetails(width, height, options);
        if (candidates.Count == 0)
        {
            return FallbackDetect(Math.Abs(width), Math.Abs(height));
        }

        return ToDetection(candidates[0]);
    }

    public static IReadOnlyList<PaperDetection> DetectCandidates(double width, double height)
    {
        return DetectCandidates(width, height, DefaultDetectionOptions);
    }

    public static IReadOnlyList<PaperDetection> DetectCandidates(double width, double height, DetectionOptions options)
    {
        var details = GetCandidateDetails(width, height, options);
        if (details.Count == 0)
        {
            return new PaperDetection[0];
        }

        var scoreLimit = Math.Min(0.04d, Math.Max(0.015d, details[0].Score + 0.015d));
        var filtered = details
            .Where(candidate => candidate.Score <= scoreLimit)
            .GroupBy(
                candidate => $"{candidate.Paper.Name}|{candidate.IsLong}|{candidate.Scale:0.########}|{candidate.PaperWidthMm:0.##}|{candidate.PaperHeightMm:0.##}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .Select(ToDetection)
            .ToList();

        // 兼容旧调用：没有指定空间相关优先级时仍沿用 1:100 优先；矩形框批打由专用参数控制 1:100/1:1 顺序。
        var indexOf1To100 = options.PreferredScaleValues == null && !options.PreferredScaleValue.HasValue
            ? filtered.FindIndex(x => Math.Abs(x.ScaleValue - 100d) < 0.001)
            : -1;
        if (indexOf1To100 > 0)
        {
            var preferred = filtered[indexOf1To100];
            filtered.RemoveAt(indexOf1To100);
            filtered.Insert(0, preferred);
        }

        return filtered;
    }

    /// <summary>
    /// 创建图框库批打专用识别参数。短边按设置中的毫米容差匹配；长边只要超过标准长边，
    /// 就保留实测物理尺寸并标记为动态纸张。布局空间优先 1:1，模型空间沿用常用的 1:100 优先。
    /// </summary>
    public static DetectionOptions CreateTitleBlockBatchOptions(double paperMatchToleranceMm, bool isPaperSpace)
    {
        return new DetectionOptions
        {
            AllowArbitraryLongSide = true,
            LongPaperShortSideToleranceMm = Math.Max(0.05d, paperMatchToleranceMm),
            PreferredScaleValue = isPaperSpace ? 1d : 100d
        };
    }

    /// <summary>
    /// 创建矩形框批打专用识别参数。短边和 1/8 模数的误差都使用设置中的毫米容差；
    /// 模型空间优先按 1:100 解释，布局空间优先按 1:1 解释，同时把另一常用比例排在其余比例之前。
    /// </summary>
    public static DetectionOptions CreateRectangleBatchOptions(double paperMatchToleranceMm, bool isPaperSpace)
    {
        var toleranceMm = Math.Max(0d, paperMatchToleranceMm);
        return new DetectionOptions
        {
            UseRectangleShortSideMatching = true,
            LongPaperShortSideToleranceMm = toleranceMm,
            PreferredScaleValues = isPaperSpace
                ? new[] { 1d, 100d }
                : new[] { 100d, 1d }
        };
    }

    private static List<PaperCandidate> GetCandidateDetails(double width, double height, DetectionOptions options)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var candidates = new List<PaperCandidate>();
        foreach (var paper in Standards)
        {
            foreach (var scale in CommonScales)
            {
                AddCandidate(candidates, paper, scale, actualWidth, actualHeight, options);
            }
        }

        return candidates
            // 图框库固定纸张优先：物理尺寸与录入纸张一致的候选排在最前，避免零头误差顶掉录入纸张。
            .OrderBy(x => MatchesPreferredPaper(x, options) ? 0 : 1)
            // 布局中的物理图框应优先按 1:1 解释，避免 297mm 短边被误判成 A1 的 2:1。
            .ThenBy(x => GetScalePreferenceRank(x.Scale, options))
            .ThenBy(x => x.Score)
            .ThenBy(x => x.IsLong ? 0 : 1)
            .ThenByDescending(x => x.Scale)
            .ThenBy(x => Array.IndexOf(Standards, x.Paper))
            .ToList();
    }

    private static PaperDetection ToDetection(PaperCandidate best)
    {
        var paperName = best.IsLong
            ? best.Paper.Name + "+" + (best.RequiresCustomPaper
                ? ContinuousLongPaperExtension(best.PaperWidthMm, best.PaperHeightMm, best.Paper)
                : LongPaperExtension(best.PaperWidthMm, best.PaperHeightMm, best.Paper))
            : best.Paper.Name;
        var paperSizeText = $"{best.PaperWidthMm:0.##} x {best.PaperHeightMm:0.##} mm";
        return new PaperDetection
        {
            PaperName = paperName,
            ScaleValue = best.Scale,
            ScaleText = ToScaleText(best.Scale),
            IsLong = best.IsLong,
            PaperWidthMm = best.PaperWidthMm,
            PaperHeightMm = best.PaperHeightMm,
            RequiresCustomPaper = best.RequiresCustomPaper,
            Note = best.IsLong
                ? $"{best.Paper.Name} 加长，{best.Reason}，输出纸张 {paperSizeText}"
                : $"{best.Paper.Name} 标准图幅，{best.Reason}，输出纸张 {paperSizeText}"
        };
    }

    public static (double Width, double Height) GetDefaultSize(string paperName, double currentWidth = 0, double currentHeight = 0)
    {
        var plusIndex = paperName.IndexOf('+');
        var baseName = plusIndex >= 0 ? paperName.Substring(0, plusIndex) : paperName;
        var standard = Standards.FirstOrDefault(x => string.Equals(x.Name, baseName, StringComparison.OrdinalIgnoreCase));
        if (standard == null)
        {
            return (Math.Max(currentWidth, 1), Math.Max(currentHeight, 1));
        }

        var landscape = currentWidth <= 0 || currentHeight <= 0 || currentWidth >= currentHeight;
        if (plusIndex < 0)
        {
            return landscape ? (standard.LongSide, standard.ShortSide) : (standard.ShortSide, standard.LongSide);
        }

        var measuredLong = Math.Max(currentWidth, currentHeight);
        var longSide = measuredLong > standard.LongSide
            ? SnapLongSide(measuredLong, standard.LongSide)
            : standard.LongSide * MinimumLongPaperUnits / (double)LongPaperIncrementDenominator;
        return landscape ? (longSide, standard.ShortSide) : (standard.ShortSide, longSide);
    }

    private static void AddCandidate(List<PaperCandidate> candidates, StandardPaper paper, double scale, double actualWidth, double actualHeight, DetectionOptions options)
    {
        var widthMm = actualWidth / scale;
        var heightMm = actualHeight / scale;

        AddOrientation(candidates, paper, scale, widthMm, heightMm, actualWidth, actualHeight, paper.LongSide, paper.ShortSide, options);
        AddOrientation(candidates, paper, scale, widthMm, heightMm, actualWidth, actualHeight, paper.ShortSide, paper.LongSide, options);
    }

    private static void AddOrientation(
        List<PaperCandidate> candidates,
        StandardPaper paper,
        double scale,
        double widthMm,
        double heightMm,
        double actualWidth,
        double actualHeight,
        double standardWidth,
        double standardHeight,
        DetectionOptions options)
    {
        if (options.UseRectangleShortSideMatching)
        {
            AddRectangleShortSideCandidate(
                candidates,
                paper,
                scale,
                widthMm,
                heightMm,
                actualWidth,
                actualHeight,
                options);
            return;
        }

        var widthError = RelativeError(widthMm, standardWidth);
        var heightError = RelativeError(heightMm, standardHeight);
        if (widthError <= StandardPaperTolerance && heightError <= StandardPaperTolerance)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = Math.Max(widthError, heightError),
                IsLong = false,
                PaperWidthMm = standardWidth,
                PaperHeightMm = standardHeight,
                Reason = $"按常用比例匹配，宽误差 {widthError:P1}，高误差 {heightError:P1}，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
            });
        }

        var expectedShort = Math.Min(standardWidth, standardHeight);
        var expectedLong = Math.Max(standardWidth, standardHeight);
        var actualShort = Math.Min(widthMm, heightMm);
        var actualLong = Math.Max(widthMm, heightMm);
        var shortError = RelativeError(actualShort, expectedShort);
        var isLong = actualLong > expectedLong * 1.03;
        if (options.AllowArbitraryLongSide)
        {
            var shortErrorMm = Math.Abs(actualShort - expectedShort);
            var shortToleranceMm = options.LongPaperShortSideToleranceMm ?? 0d;
            // 任意加长纸只锁定标准短边；长边无模数限制，并保留图框实测宽高以生成精确 PMP 纸张。
            // 长边超出标准长边一个匹配容差以上才算加长；容差内的零头（如图框含线宽多出的 0.5mm）仍按标准幅面处理。
            if (actualLong > expectedLong + shortToleranceMm && shortErrorMm <= shortToleranceMm)
            {
                candidates.Add(new PaperCandidate
                {
                    Paper = paper,
                    Scale = scale,
                    Score = shortError,
                    IsLong = true,
                    RequiresCustomPaper = true,
                    PaperWidthMm = widthMm,
                    PaperHeightMm = heightMm,
                    Reason = $"短边与 {expectedShort:0.##}mm 的误差为 {shortErrorMm:0.###}mm（容差 {shortToleranceMm:0.###}mm），长边按实测 {actualLong:0.######}mm 动态生成"
                });
            }

            return;
        }

        if (!isLong || shortError > options.LongPaperShortSideTolerance)
        {
            return;
        }

        var snappedLong = SnapLongSide(actualLong, expectedLong);
        var longError = RelativeError(actualLong, snappedLong);
        if (longError > LongPaperSnapTolerance)
        {
            return;
        }

        var landscape = widthMm >= heightMm;
        candidates.Add(new PaperCandidate
        {
            Paper = paper,
            Scale = scale,
            Score = shortError + longError * 0.35,
            IsLong = true,
            PaperWidthMm = landscape ? snappedLong : expectedShort,
            PaperHeightMm = landscape ? expectedShort : snappedLong,
            Reason = $"短边锁定 {expectedShort:0.##}mm，长边按标准长边的 1/8 倍数匹配为 {snappedLong:0.##}mm，短边误差 {shortError:P1}，长边误差 {longError:P1}，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
        });
    }

    /// <summary>
    /// 矩形框只以物理短边确定 A0~A4 及比例。长边在标准长度容差内按标准纸，
    /// 超过标准长度后先尝试 1/8 模数；只有模数误差超过设置容差时才生成任意动态纸张。
    /// </summary>
    private static void AddRectangleShortSideCandidate(
        List<PaperCandidate> candidates,
        StandardPaper paper,
        double scale,
        double widthMm,
        double heightMm,
        double actualWidth,
        double actualHeight,
        DetectionOptions options)
    {
        var actualShort = Math.Min(widthMm, heightMm);
        var actualLong = Math.Max(widthMm, heightMm);
        var toleranceMm = options.LongPaperShortSideToleranceMm ?? 0d;
        var shortErrorMm = Math.Abs(actualShort - paper.ShortSide);
        if (shortErrorMm > toleranceMm)
        {
            return;
        }

        var longErrorFromStandardMm = Math.Abs(actualLong - paper.LongSide);
        var landscape = widthMm >= heightMm;
        if (longErrorFromStandardMm <= toleranceMm)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = Math.Max(
                    RelativeError(actualShort, paper.ShortSide),
                    RelativeError(actualLong, paper.LongSide)),
                IsLong = false,
                PaperWidthMm = landscape ? paper.LongSide : paper.ShortSide,
                PaperHeightMm = landscape ? paper.ShortSide : paper.LongSide,
                Reason = $"短边误差 {shortErrorMm:0.###}mm、长边误差 {longErrorFromStandardMm:0.###}mm，均在设置容差 {toleranceMm:0.###}mm 内，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
            });
            return;
        }

        // 长边小于标准幅面且差值超出容差时不是加长图，不能仅凭短边把普通窄矩形误识别为图框。
        if (actualLong + toleranceMm < paper.LongSide)
        {
            return;
        }

        var snappedLong = SnapLongSide(actualLong, paper.LongSide);
        var modularErrorMm = Math.Abs(actualLong - snappedLong);
        if (modularErrorMm <= toleranceMm)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = RelativeError(actualShort, paper.ShortSide)
                    + RelativeError(actualLong, snappedLong) * 0.35d,
                IsLong = true,
                PaperWidthMm = landscape ? snappedLong : paper.ShortSide,
                PaperHeightMm = landscape ? paper.ShortSide : snappedLong,
                Reason = $"短边误差 {shortErrorMm:0.###}mm，长边命中 1/8 模数 {snappedLong:0.###}mm，模数误差 {modularErrorMm:0.###}mm（设置容差 {toleranceMm:0.###}mm）"
            });
            return;
        }

        candidates.Add(new PaperCandidate
        {
            Paper = paper,
            Scale = scale,
            Score = RelativeError(actualShort, paper.ShortSide),
            IsLong = true,
            RequiresCustomPaper = true,
            PaperWidthMm = widthMm,
            PaperHeightMm = heightMm,
            Reason = $"短边误差 {shortErrorMm:0.###}mm（设置容差 {toleranceMm:0.###}mm），长边偏离最近 1/8 模数 {modularErrorMm:0.###}mm，按实测 {actualLong:0.######}mm 动态生成"
        });
    }

    private static double SnapLongSide(double measuredLong, double standardLong)
    {
        var units = Math.Max(MinimumLongPaperUnits, (int)Math.Round(measuredLong / standardLong * LongPaperIncrementDenominator, MidpointRounding.AwayFromZero));
        return standardLong * units / (double)LongPaperIncrementDenominator;
    }

    /// <summary>
    /// 候选纸张的物理尺寸是否与图框库固定纸张一致（两个方向都允许），
    /// 容差沿用短边毫米容差，未设置时按 1mm。
    /// </summary>
    private static bool MatchesPreferredPaper(PaperCandidate candidate, DetectionOptions options)
    {
        if (options.PreferredPaperWidthMm <= 0d || options.PreferredPaperHeightMm <= 0d)
        {
            return false;
        }

        var toleranceMm = Math.Max(0.05d, options.LongPaperShortSideToleranceMm ?? 1d);
        var direct =
            Math.Abs(candidate.PaperWidthMm - options.PreferredPaperWidthMm) <= toleranceMm
            && Math.Abs(candidate.PaperHeightMm - options.PreferredPaperHeightMm) <= toleranceMm;
        if (direct)
        {
            return true;
        }

        return Math.Abs(candidate.PaperWidthMm - options.PreferredPaperHeightMm) <= toleranceMm
            && Math.Abs(candidate.PaperHeightMm - options.PreferredPaperWidthMm) <= toleranceMm;
    }

    private static int GetScalePreferenceRank(double scale, DetectionOptions options)
    {
        if (options.PreferredScaleValues != null)
        {
            for (var i = 0; i < options.PreferredScaleValues.Count; i++)
            {
                if (Math.Abs(scale - options.PreferredScaleValues[i]) < 0.001d)
                {
                    return i;
                }
            }

            return options.PreferredScaleValues.Count;
        }

        return options.PreferredScaleValue.HasValue
            && Math.Abs(scale - options.PreferredScaleValue.Value) < 0.001d
            ? 0
            : 1;
    }

    private static string LongPaperExtension(double paperWidthMm, double paperHeightMm, StandardPaper standard)
    {
        var longSide = Math.Max(paperWidthMm, paperHeightMm);
        var units = (int)Math.Round(longSide / standard.LongSide * LongPaperIncrementDenominator, MidpointRounding.AwayFromZero);
        var ext = units - LongPaperIncrementDenominator;
        return ext <= 0 ? "" : FormatLongPaperExtension(ext);
    }

    private static string ContinuousLongPaperExtension(double paperWidthMm, double paperHeightMm, StandardPaper standard)
    {
        var extension = Math.Max(paperWidthMm, paperHeightMm) / standard.LongSide - 1d;
        return Math.Max(0d, extension).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string FormatLongPaperExtension(int extensionUnits)
    {
        if (extensionUnits <= 0)
        {
            return "";
        }

        // 延伸量即使大于 1 也必须约分，例如 12/8 应显示为 3/2，而不是保留假分数原值。
        var divisor = GreatestCommonDivisor(extensionUnits, LongPaperIncrementDenominator);
        return $"{extensionUnits / divisor}/{LongPaperIncrementDenominator / divisor}";
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return Math.Abs(left);
    }

    private static PaperDetection FallbackDetect(double actualWidth, double actualHeight)
    {
        var shortSide = Math.Min(actualWidth, actualHeight);
        var longSide = Math.Max(actualWidth, actualHeight);
        return new PaperDetection
        {
            Note = $"未匹配到 A0~A4，实际尺寸 {longSide:0.##} x {shortSide:0.##}"
        };
    }

    private static double RelativeError(double value, double target)
    {
        return Math.Abs(value - target) / Math.Max(target, 1);
    }

    private static string ToScaleText(double scale)
    {
        if (Math.Abs(scale - 1) < 0.001)
        {
            return "1:1";
        }

        if (scale > 1)
        {
            return "1:" + scale.ToString("0.###", CultureInfo.InvariantCulture);
        }

        return (1d / scale).ToString("0.###", CultureInfo.InvariantCulture) + ":1";
    }
}
