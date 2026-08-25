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
        /// <summary>
        /// 标准 A0~A4 宽高的绝对匹配容差（毫米）。图框块批打设置此值后，
        /// 标准纸张只按“常规 → 纸张匹配容差(mm)”判断，不再使用固定 4% 相对误差。
        /// null 保留其他调用入口原有的相对误差规则。
        /// </summary>
        public double? StandardPaperToleranceMm { get; set; }
        /// <summary>加长图长边吸附到最近 1/8 模数的容差（毫米）；null 表示不吸附，按实测生成动态纸。</summary>
        public double? LongPaperSnapToleranceMm { get; set; }
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
        /// 可自由拉伸模板的基础图幅，例如 A2+ 只允许按 A2 解释；长边仍按当前实例重新计算。
        /// </summary>
        public string PreferredPaperBaseName { get; set; } = "";
        /// <summary>
        /// 矩形框专用：只用短边匹配标准幅面；长边先按标准尺寸或 1/8 加长模数吸附，
        /// 超过容差时保留实测长边并转为任意动态纸张。
        /// </summary>
        public bool UseRectangleShortSideMatching { get; set; }
        /// <summary>
        /// 可自由拉伸图框录入专用：纸张只保存 A1+ 这类基础图幅，
        /// 具体加长分数和物理尺寸由每个块参照的当前外框重新识别。
        /// 此类动态块按常用比例识别，不会出现任意比例套标准图幅的情况。
        /// </summary>
        public bool IncludeGenericDynamicTitleBlockPaper { get; set; }
        /// <summary>
        /// 在内置比例之外追加参与匹配的自定义比例（设置中的“比例设置”列表）；null 或空表示只用内置比例。
        /// </summary>
        public IReadOnlyList<double>? ExtraScales { get; set; }
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

    /// <summary>内置支持的比例列表（只读）；用户自定义比例见 <see cref="AppSettings.CustomScales"/>。</summary>
    public static IReadOnlyList<double> BuiltInScales => CommonScales;

    /// <summary>
    /// 任意纸张的库记录名称约定：外框识别不到 A4~A0 及其加长纸张时，
    /// 按用户输入的绘图比例换算出的纸张（图面尺寸 / 比例 = 纸张毫米尺寸）。
    /// 扫描打印时固定使用库中宽高，并按当前图框外框重新计算比例。
    /// </summary>
    public const string CustomPaperName = "自定义";

    /// <summary>
    /// 图纸长宽比与标准 A0~A4（及 1/8 模数加长图）长宽比的绝对误差上限。
    /// 命中后允许以任意比例将该图幅解释为对应标准或加长纸张。
    /// </summary>
    public const double AspectRatioMatchTolerance = 0.01d;

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

    /// <summary>
    /// 按图纸长宽比匹配标准 A0~A4 及其 1/8 模数加长图，不要求比例落在常用比例列表中。
    /// 误差为 |图纸长边/短边 − 纸张长边/短边|，小于 <paramref name="maxAspectRatioError"/> 即命中。
    /// 返回项的物理尺寸为对应标准或加长图幅（方向与图纸一致），比例按 图面短边 / 纸张短边 计算。
    /// 列表先按 A4→A0 标准图幅，再按同序加长图幅排列。
    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectByAspectRatio(
        double width,
        double height,
        double maxAspectRatioError = AspectRatioMatchTolerance)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var actualShort = Math.Min(actualWidth, actualHeight);
        var actualLong = Math.Max(actualWidth, actualHeight);
        if (actualShort <= 1e-9d)
        {
            return new PaperDetection[0];
        }

        var drawingRatio = actualLong / actualShort;
        var landscape = actualWidth >= actualHeight;
        var matches = new List<PaperDetection>();
        AddAspectRatioMatches(matches, actualShort, actualLong, drawingRatio, landscape, maxAspectRatioError, elongated: false);
        AddAspectRatioMatches(matches, actualShort, actualLong, drawingRatio, landscape, maxAspectRatioError, elongated: true);
        return matches;
    }

    /// <summary>
    /// 按 A4→A0 加入长宽比命中的标准图幅或加长图幅。
    /// 加长图先按短边反推任意比例，再把还原长边吸附到 1/8 模数；吸附后的长宽比仍须落入容差。
    /// </summary>
    private static void AddAspectRatioMatches(
        List<PaperDetection> matches,
        double actualShort,
        double actualLong,
        double drawingRatio,
        bool landscape,
        double maxAspectRatioError,
        bool elongated)
    {
        for (var i = Standards.Length - 1; i >= 0; i--)
        {
            var paper = Standards[i];
            var scale = actualShort / paper.ShortSide;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
            {
                continue;
            }

            var restoredLongMm = actualLong / scale;
            double paperLongMm;
            double ratioError;
            if (elongated)
            {
                // SnapLongSide 的下限是 +1/8，标准图幅不能走加长分支，否则会被硬吸附成加长图。
                if (restoredLongMm <= paper.LongSide * 1.03d)
                {
                    continue;
                }

                paperLongMm = SnapLongSide(restoredLongMm, paper.LongSide);
                ratioError = Math.Abs(drawingRatio - paperLongMm / paper.ShortSide);
            }
            else
            {
                paperLongMm = paper.LongSide;
                ratioError = Math.Abs(drawingRatio - paper.LongSide / paper.ShortSide);
            }

            if (ratioError >= maxAspectRatioError)
            {
                continue;
            }

            var paperWidthMm = landscape ? paperLongMm : paper.ShortSide;
            var paperHeightMm = landscape ? paper.ShortSide : paperLongMm;
            var paperName = elongated
                ? paper.Name + "+" + LongPaperExtension(paperWidthMm, paperHeightMm, paper)
                : paper.Name;
            matches.Add(new PaperDetection
            {
                PaperName = paperName,
                ScaleValue = scale,
                ScaleText = ToScaleText(scale),
                IsLong = elongated,
                PaperWidthMm = paperWidthMm,
                PaperHeightMm = paperHeightMm,
                Note = elongated
                    ? $"{paper.Name} 加长图，长宽比误差 {ratioError:0.####}（容差 {maxAspectRatioError}），"
                      + $"长边吸附 1/8 模数 {paperLongMm:0.###}mm，按任意比例 {ToScaleText(scale)}"
                    : $"{paper.Name} 标准图幅，长宽比误差 {ratioError:0.####}（容差 {maxAspectRatioError}），"
                      + $"按任意比例 {ToScaleText(scale)} 还原长边 {restoredLongMm:0.###}mm"
            });
        }
    }

    /// <summary>
    /// 在 <see cref="DetectByAspectRatio"/> 的结果中选出默认图幅：
    /// 先比长宽比误差，再比比例是否接近整数，最后偏向较小图幅。
    /// </summary>
    public static int IndexOfPreferredAspectRatioPaper(
        double width,
        double height,
        IReadOnlyList<PaperDetection> papers)
    {
        var actualShort = Math.Min(Math.Abs(width), Math.Abs(height));
        var actualLong = Math.Max(Math.Abs(width), Math.Abs(height));
        if (papers.Count == 0 || actualShort <= 1e-9d)
        {
            return -1;
        }

        var drawingRatio = actualLong / actualShort;
        var bestIndex = 0;
        var bestError = double.MaxValue;
        var bestRoundness = double.MaxValue;
        var bestShortSide = double.MaxValue;
        for (var i = 0; i < papers.Count; i++)
        {
            var paper = papers[i];
            var paperShort = Math.Min(paper.PaperWidthMm, paper.PaperHeightMm);
            var paperLong = Math.Max(paper.PaperWidthMm, paper.PaperHeightMm);
            if (paperShort <= 1e-9d)
            {
                continue;
            }

            var error = Math.Abs(drawingRatio - paperLong / paperShort);
            var roundness = ScaleIntegerDeviation(paper.ScaleValue);
            if (error < bestError - 1e-12d
                || (Math.Abs(error - bestError) <= 1e-12d && roundness < bestRoundness - 1e-9d)
                || (Math.Abs(error - bestError) <= 1e-12d
                    && Math.Abs(roundness - bestRoundness) <= 1e-9d
                    && paperShort < bestShortSide))
            {
                bestError = error;
                bestRoundness = roundness;
                bestShortSide = paperShort;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>比例与最近整数的相对偏差；放大比例按倒数判断，便于 4:1 这类取值靠近整数。</summary>
    private static double ScaleIntegerDeviation(double scale)
    {
        var reference = scale >= 1d ? scale : 1d / Math.Max(scale, 1e-9d);
        var rounded = Math.Round(reference);
        if (rounded < 1d)
        {
            return Math.Abs(reference - 1d);
        }

        return Math.Abs(reference - rounded) / rounded;
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

        var detected = ToDetection(candidates[0]);
        return options.IncludeGenericDynamicTitleBlockPaper
            ? ToGenericDynamicTitleBlockPaper(detected)
            : detected;
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

        if (!options.IncludeGenericDynamicTitleBlockPaper)
        {
            return filtered;
        }

        // 可自由拉伸块只保存基础图幅名，不能让当前实例的 A1+1/2 等长度锁死后续实例。
        return filtered
            .Select(ToGenericDynamicTitleBlockPaper)
            .GroupBy(
                paper => $"{paper.PaperName}|{paper.ScaleValue:0.########}|{paper.PaperWidthMm:0.##}|{paper.PaperHeightMm:0.##}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .ToList();
    }

    /// <summary>
    /// 返回全部有效候选；若当前尺寸不满足标准/加长纸规则，则生成一个包含完整尺寸的自定义兜底项。
    /// 新增图框需要始终允许用户继续录入，而矩形框扫描仍可使用 DetectCandidates 的空结果过滤非图框。
    /// </summary>
    public static IReadOnlyList<PaperDetection> DetectCandidatesOrFallback(
        double width,
        double height,
        DetectionOptions options)
    {
        var candidates = DetectCandidates(width, height, options);
        if (candidates.Count > 0)
        {
            return candidates;
        }

        // 界面已不再提供独立宽/高输入框，因此兜底项也必须携带可保存、可打印的完整尺寸。
        // 比例优先按常用整数比例推测；仍无法推测时按 1:1 保留实测尺寸。
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var scale = GuessScale(actualWidth, actualHeight);
        if (scale <= 0)
        {
            scale = 1;
        }

        return new[]
        {
            new PaperDetection
            {
                PaperName = "自定义",
                PaperWidthMm = actualWidth / scale,
                PaperHeightMm = actualHeight / scale,
                ScaleValue = scale,
                ScaleText = ToScaleText(scale),
                RequiresCustomPaper = true,
                Note = $"未匹配到标准或模数纸张，按实测尺寸生成自定义纸张（推测比例 {ToScaleText(scale)}）"
            }
        };
    }

    /// <summary>统一纸张候选的界面显示，避免不同打印入口对名称、尺寸和比例采用不同格式。</summary>
    public static string FormatOption(PaperDetection paper)
    {
        return $"{paper.PaperName} | {paper.PaperWidthMm:0.##}×{paper.PaperHeightMm:0.##}mm | {paper.ScaleText}";
    }

    /// <summary>
    /// 创建图框库批打专用识别参数。短边按设置中的毫米容差匹配；长边超过标准长边后，
    /// 与最近 1/8 模数的差距在吸附容差内按标准加长图输出，超出才保留实测尺寸生成动态纸张。
    /// 布局空间优先 1:1，模型空间沿用常用的 1:100 优先。
    /// </summary>
    public static DetectionOptions CreateTitleBlockBatchOptions(double paperMatchToleranceMm, bool isPaperSpace, double? longPaperSnapToleranceMm = null, IReadOnlyList<double>? customScales = null)
    {
        return new DetectionOptions
        {
            AllowArbitraryLongSide = true,
            StandardPaperToleranceMm = Math.Max(0d, paperMatchToleranceMm),
            LongPaperShortSideToleranceMm = Math.Max(0d, paperMatchToleranceMm),
            LongPaperSnapToleranceMm = longPaperSnapToleranceMm,
            PreferredScaleValue = isPaperSpace ? 1d : 100d,
            ExtraScales = customScales
        };
    }

    /// <summary>
    /// 图框块批打专用的任意比例识别。比例只由“当前图框短边 / 录入纸张短边”反推，
    /// 不经过内置或用户比例列表，因此同时支持 1:143、10:1、2.1:1 等任意放大与缩小比例。
    /// 取得比例后再还原当前物理长边：标准长度按录入基础图幅输出，加长长度继续执行
    /// 1/8 模数吸附，不能吸附时保留实测长边并要求注册任意纸张。
    /// </summary>
    public static bool TryDetectTitleBlockAtArbitraryScale(
        double frameWidth,
        double frameHeight,
        string recordedPaperName,
        double recordedPaperWidthMm,
        double recordedPaperHeightMm,
        double paperMatchToleranceMm,
        double? longPaperSnapToleranceMm,
        out PaperDetection detected)
    {
        detected = new PaperDetection();
        var actualWidth = Math.Abs(frameWidth);
        var actualHeight = Math.Abs(frameHeight);
        var paperWidth = Math.Abs(recordedPaperWidthMm);
        var paperHeight = Math.Abs(recordedPaperHeightMm);
        if (actualWidth <= 1e-9d
            || actualHeight <= 1e-9d
            || paperWidth <= 1e-9d
            || paperHeight <= 1e-9d)
        {
            return false;
        }

        var actualShort = Math.Min(actualWidth, actualHeight);
        var actualLong = Math.Max(actualWidth, actualHeight);
        var recordedShort = Math.Min(paperWidth, paperHeight);
        var scale = actualShort / recordedShort;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(recordedPaperName)
            ? CustomPaperName
            : recordedPaperName.Trim();
        var scaleText = ToScaleText(scale);

        // 真正的任意纸张没有 A0~A4 基础短边，仍固定使用录入宽高；这里只把比例改为
        // 短边反推，避免非标准长边的细小误差把 2.1:1 平均成另一个比例。
        if (string.Equals(name, CustomPaperName, StringComparison.OrdinalIgnoreCase))
        {
            detected = new PaperDetection
            {
                PaperName = name,
                ScaleValue = scale,
                ScaleText = scaleText,
                PaperWidthMm = recordedPaperWidthMm,
                PaperHeightMm = recordedPaperHeightMm,
                RequiresCustomPaper = true,
                Note = $"任意纸张沿用图框库录入尺寸 {paperWidth:0.##} x {paperHeight:0.##} mm；按短边识别比例 {scaleText}"
            };
            return true;
        }

        var toleranceMm = Math.Max(0d, paperMatchToleranceMm);
        var standard = ResolveRecordedTitleBlockPaper(name, recordedShort, toleranceMm);
        if (standard == null)
        {
            // 兼容旧图框库中的非标准命名：只要录入宽高完整就仍可打印，纸张按录入尺寸注册。
            detected = new PaperDetection
            {
                PaperName = name,
                ScaleValue = scale,
                ScaleText = scaleText,
                IsLong = name.IndexOf('+') > 0,
                PaperWidthMm = recordedPaperWidthMm,
                PaperHeightMm = recordedPaperHeightMm,
                RequiresCustomPaper = true,
                Note = $"图框库纸张未对应 A0~A4，沿用录入尺寸 {paperWidth:0.##} x {paperHeight:0.##} mm；按短边识别比例 {scaleText}"
            };
            return true;
        }

        var restoredLongMm = actualLong / scale;
        var landscape = actualWidth >= actualHeight;
        if (restoredLongMm <= standard.LongSide + toleranceMm)
        {
            detected = new PaperDetection
            {
                PaperName = standard.Name,
                ScaleValue = scale,
                ScaleText = scaleText,
                PaperWidthMm = landscape ? standard.LongSide : standard.ShortSide,
                PaperHeightMm = landscape ? standard.ShortSide : standard.LongSide,
                Note = $"{standard.Name} 标准图幅，按录入短边 {recordedShort:0.###}mm 识别任意比例 {scaleText}，还原长边 {restoredLongMm:0.###}mm"
            };
            return true;
        }

        var snappedLongMm = SnapLongSide(restoredLongMm, standard.LongSide);
        var snapErrorMm = Math.Abs(restoredLongMm - snappedLongMm);
        var snapToleranceMm = Math.Max(0d, longPaperSnapToleranceMm ?? toleranceMm);
        var canSnap = snapErrorMm <= snapToleranceMm;
        var outputLongMm = canSnap ? snappedLongMm : restoredLongMm;
        var candidate = new PaperCandidate
        {
            Paper = standard,
            Scale = scale,
            Score = RelativeError(restoredLongMm, outputLongMm),
            IsLong = true,
            RequiresCustomPaper = !canSnap,
            PaperWidthMm = landscape ? outputLongMm : standard.ShortSide,
            PaperHeightMm = landscape ? standard.ShortSide : outputLongMm,
            Reason = canSnap
                ? $"按录入短边 {recordedShort:0.###}mm 识别任意比例 {scaleText}，长边吸附 1/8 模数 {snappedLongMm:0.###}mm（误差 {snapErrorMm:0.###}mm，吸附容差 {snapToleranceMm:0.###}mm）"
                : $"按录入短边 {recordedShort:0.###}mm 识别任意比例 {scaleText}，长边按实测 {restoredLongMm:0.######}mm 动态生成"
        };
        detected = ToDetection(candidate);
        return true;
    }

    /// <summary>
    /// 创建矩形框批打专用识别参数。短边匹配用纸张匹配容差，长边 1/8 模数吸附用专用吸附容差；
    /// 模型空间优先按 1:100 解释，布局空间优先按 1:1 解释，同时把另一常用比例排在其余比例之前。
    /// </summary>
    public static DetectionOptions CreateRectangleBatchOptions(double paperMatchToleranceMm, bool isPaperSpace, double? longPaperSnapToleranceMm = null, IReadOnlyList<double>? customScales = null)
    {
        var toleranceMm = Math.Max(0d, paperMatchToleranceMm);
        return new DetectionOptions
        {
            UseRectangleShortSideMatching = true,
            LongPaperShortSideToleranceMm = toleranceMm,
            LongPaperSnapToleranceMm = longPaperSnapToleranceMm,
            PreferredScaleValues = isPaperSpace
                ? new[] { 1d, 100d }
                : new[] { 100d, 1d },
            ExtraScales = customScales
        };
    }

    private static List<PaperCandidate> GetCandidateDetails(double width, double height, DetectionOptions options)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var candidates = new List<PaperCandidate>();
        var scales = MergeScales(options.ExtraScales);
        foreach (var paper in Standards)
        {
            foreach (var scale in scales)
            {
                AddCandidate(candidates, paper, scale, actualWidth, actualHeight, options);
            }
        }

        return candidates
            // 图框库固定纸张优先：物理尺寸与录入纸张一致的候选排在最前，避免零头误差顶掉录入纸张。
            .OrderBy(x => MatchesPreferredPaper(x, options) ? 0 : 1)
            // A1+/A2+ 已经明确记录了基础图幅，扫描时只重新判断加长长度，不能换比例猜成 A3/A4。
            .ThenBy(x => MatchesPreferredPaperBase(x, options) ? 0 : 1)
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

    /// <summary>
    /// 可自由拉伸图框只保留 A1+ 这类基础图幅名，具体加长长度扫描时再判定。
    /// </summary>
    private static PaperDetection ToGenericDynamicTitleBlockPaper(PaperDetection paper)
    {
        var plusIndex = paper.PaperName.IndexOf('+');
        var baseName = plusIndex > 0 ? paper.PaperName.Substring(0, plusIndex) : paper.PaperName;
        if (!Standards.Any(standard => string.Equals(standard.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return paper;
        }

        return new PaperDetection
        {
            PaperName = baseName + "+",
            ScaleText = paper.ScaleText,
            ScaleValue = paper.ScaleValue,
            IsLong = true,
            PaperWidthMm = paper.PaperWidthMm,
            PaperHeightMm = paper.PaperHeightMm,
            RequiresCustomPaper = paper.RequiresCustomPaper,
            Note = $"可自由拉伸图框按 {baseName}+ 录入，扫描时再判定实际纸张"
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
        var widthErrorMm = Math.Abs(widthMm - standardWidth);
        var heightErrorMm = Math.Abs(heightMm - standardHeight);
        var matchesStandardPaper = options.StandardPaperToleranceMm.HasValue
            ? widthErrorMm <= options.StandardPaperToleranceMm.Value
              && heightErrorMm <= options.StandardPaperToleranceMm.Value
            : widthError <= StandardPaperTolerance
              && heightError <= StandardPaperTolerance;
        if (matchesStandardPaper)
        {
            // 图框块批打只显示常规设置中的毫米容差，诊断信息也必须反映实际采用的规则，
            // 避免用户看到已取消的 4% 判断仍误以为它参与了识别。
            var reason = options.StandardPaperToleranceMm.HasValue
                ? $"按常用比例匹配，宽误差 {widthErrorMm:0.###}mm，高误差 {heightErrorMm:0.###}mm，容差 {options.StandardPaperToleranceMm.Value:0.###}mm，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
                : $"按常用比例匹配，宽误差 {widthError:P1}，高误差 {heightError:P1}，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}";
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = Math.Max(widthError, heightError),
                IsLong = false,
                PaperWidthMm = standardWidth,
                PaperHeightMm = standardHeight,
                Reason = reason
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
            // 任意加长纸只锁定标准短边；长边与最近 1/8 模数的差距在吸附容差内时按标准加长图输出
            // （吸附后的尺寸同时用于 PMP 注册和输出显示名），超出容差才保留实测宽高生成动态纸张。
            // 长边超出标准长边一个匹配容差以上才算加长；容差内的零头（如图框含线宽多出的 0.5mm）仍按标准幅面处理。
            if (actualLong > expectedLong + shortToleranceMm && shortErrorMm <= shortToleranceMm)
            {
                var snappedLongMm = SnapLongSide(actualLong, expectedLong);
                var snapErrorMm = Math.Abs(actualLong - snappedLongMm);
                var snapToleranceMm = options.LongPaperSnapToleranceMm;
                var canSnap = snapToleranceMm.HasValue && snapErrorMm <= snapToleranceMm.Value;
                var isLandscape = widthMm >= heightMm;
                candidates.Add(new PaperCandidate
                {
                    Paper = paper,
                    Scale = scale,
                    Score = shortError,
                    IsLong = true,
                    RequiresCustomPaper = !canSnap,
                    PaperWidthMm = canSnap
                        ? (isLandscape ? snappedLongMm : expectedShort)
                        : widthMm,
                    PaperHeightMm = canSnap
                        ? (isLandscape ? expectedShort : snappedLongMm)
                        : heightMm,
                    Reason = canSnap
                        ? $"短边与 {expectedShort:0.##}mm 的误差为 {shortErrorMm:0.###}mm（容差 {shortToleranceMm:0.###}mm），长边吸附 1/8 模数 {snappedLongMm:0.###}mm（误差 {snapErrorMm:0.###}mm，吸附容差 {snapToleranceMm.GetValueOrDefault():0.###}mm）"
                        : $"短边与 {expectedShort:0.##}mm 的误差为 {shortErrorMm:0.###}mm（容差 {shortToleranceMm:0.###}mm），长边按实测 {actualLong:0.######}mm 动态生成"
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
        // 长边 1/8 模数吸附优先用专用吸附容差，未设置时回退到短边匹配容差。
        var snapToleranceMm = options.LongPaperSnapToleranceMm ?? toleranceMm;
        if (modularErrorMm <= snapToleranceMm)
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
                Reason = $"短边误差 {shortErrorMm:0.###}mm，长边命中 1/8 模数 {snappedLong:0.###}mm，模数误差 {modularErrorMm:0.###}mm（吸附容差 {snapToleranceMm:0.###}mm）"
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

    private static bool MatchesPreferredPaperBase(PaperCandidate candidate, DetectionOptions options)
    {
        return !string.IsNullOrWhiteSpace(options.PreferredPaperBaseName)
            && string.Equals(
                candidate.Paper.Name,
                options.PreferredPaperBaseName,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 录入尺寸的短边是图幅身份的最终依据；名称只在旧库尺寸存在轻微异常、无法按容差命中时兜底。
    /// 这样图框名称或加长后缀陈旧时，也不会把 A2 短边误当成 A3。
    /// </summary>
    private static StandardPaper? ResolveRecordedTitleBlockPaper(string paperName, double recordedShortMm, double toleranceMm)
    {
        var nearest = Standards
            .OrderBy(paper => Math.Abs(paper.ShortSide - recordedShortMm))
            .First();
        if (Math.Abs(nearest.ShortSide - recordedShortMm) <= Math.Max(0.05d, toleranceMm))
        {
            return nearest;
        }

        var plusIndex = paperName.IndexOf('+');
        var baseName = plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
        return Standards.FirstOrDefault(paper =>
            string.Equals(paper.Name, baseName, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// 合并内置比例和用户自定义比例；剔除非法值并按 1e-6 容差去重，避免重复候选干扰排序。
    /// </summary>
    private static List<double> MergeScales(IReadOnlyList<double>? extraScales)
    {
        var scales = new List<double>(CommonScales);
        if (extraScales == null)
        {
            return scales;
        }

        foreach (var extra in extraScales)
        {
            if (extra > 0 && !scales.Any(x => Math.Abs(x - extra) < 1e-6))
            {
                scales.Add(extra);
            }
        }

        return scales;
    }

    /// <summary>
    /// 解析用户输入的比例文本。单个数 N≥1 表示 1:N；0&lt;N&lt;1 表示放大比例（0.25 → 4:1）；
    /// 也接受 "1:143"、"4:1"（含全角冒号）写法。返回值语义与内置比例一致：图面尺寸 / 比例 = 纸张毫米尺寸。
    /// </summary>
    public static bool TryParseScale(string? text, out double scale)
    {
        scale = 0;
        if (text == null)
        {
            return false;
        }

        var normalized = text.Trim().Replace('：', ':').Replace(" ", "");
        if (normalized.Length == 0)
        {
            return false;
        }
        var colonIndex = normalized.IndexOf(':');
        if (colonIndex >= 0)
        {
            var left = normalized.Substring(0, colonIndex);
            var right = normalized.Substring(colonIndex + 1);
            if (right.IndexOf(':') >= 0
                || !TryParseNumber(left, out var leftValue)
                || !TryParseNumber(right, out var rightValue)
                || leftValue <= 0
                || rightValue <= 0)
            {
                return false;
            }

            scale = rightValue / leftValue;
            return true;
        }

        if (!TryParseNumber(normalized, out var number) || number <= 0)
        {
            return false;
        }

        scale = number;
        return true;
    }

    private static bool TryParseNumber(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    /// <summary>把比例数值格式化为 "1:143" 或 "4:1" 形式的显示文本。</summary>
    public static string ToScaleText(double scale)
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
