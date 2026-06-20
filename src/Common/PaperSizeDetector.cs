using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZwcadBatchPlot;

public static class PaperSizeDetector
{
    private sealed class PaperCandidate
    {
        public StandardPaper Paper { get; set; } = null!;
        public double Scale { get; set; }
        public double Score { get; set; }
        public bool IsLong { get; set; }
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public string Reason { get; set; } = "";
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
        new("A3", 297, 420)
    };

    private static readonly double[] CommonScales =
    {
        0.01d, 0.02d, 0.025d, 0.04d, 0.05d, 0.1d, 0.2d, 0.25d, 0.4d, 0.5d,
        1d, 2d, 5d, 10d, 20d, 25d, 30d, 40d, 50d, 60d, 75d, 80d, 90d, 100d,
        120d, 125d, 150d, 200d, 250d, 300d, 400d, 500d, 600d, 1000d
    };

    public static PaperDetection Detect(double width, double height)
    {
        var candidates = GetCandidateDetails(width, height);
        if (candidates.Count == 0)
        {
            return FallbackDetect(Math.Abs(width), Math.Abs(height));
        }

        return ToDetection(candidates[0]);
    }

    public static IReadOnlyList<PaperDetection> DetectCandidates(double width, double height)
    {
        var details = GetCandidateDetails(width, height);
        if (details.Count == 0)
        {
            return Array.Empty<PaperDetection>();
        }

        var scoreLimit = Math.Min(0.04d, Math.Max(0.015d, details[0].Score + 0.015d));
        return details
            .Where(candidate => candidate.Score <= scoreLimit)
            .GroupBy(
                candidate => $"{candidate.Paper.Name}|{candidate.IsLong}|{candidate.Scale:0.########}|{candidate.PaperWidthMm:0.##}|{candidate.PaperHeightMm:0.##}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .Select(ToDetection)
            .ToList();
    }

    private static List<PaperCandidate> GetCandidateDetails(double width, double height)
    {
        var actualWidth = Math.Abs(width);
        var actualHeight = Math.Abs(height);
        var candidates = new List<PaperCandidate>();
        foreach (var paper in Standards)
        {
            foreach (var scale in CommonScales)
            {
                AddCandidate(candidates, paper, scale, actualWidth, actualHeight);
            }
        }

        return candidates
            .OrderBy(x => x.Score)
            .ThenBy(x => x.IsLong ? 0 : 1)
            .ThenByDescending(x => x.Scale)
            .ThenBy(x => Array.IndexOf(Standards, x.Paper))
            .ToList();
    }

    private static PaperDetection ToDetection(PaperCandidate best)
    {
        var paperName = best.IsLong ? best.Paper.Name + "+" : best.Paper.Name;
        var paperSizeText = $"{best.PaperWidthMm:0.##} x {best.PaperHeightMm:0.##} mm";
        return new PaperDetection
        {
            PaperName = paperName,
            ScaleValue = best.Scale,
            ScaleText = ToScaleText(best.Scale),
            IsLong = best.IsLong,
            PaperWidthMm = best.PaperWidthMm,
            PaperHeightMm = best.PaperHeightMm,
            Note = best.IsLong
                ? $"{best.Paper.Name} 加长，{best.Reason}，输出纸张 {paperSizeText}"
                : $"{best.Paper.Name} 标准图幅，{best.Reason}，输出纸张 {paperSizeText}"
        };
    }

    public static (double Width, double Height) GetDefaultSize(string paperName, double currentWidth = 0, double currentHeight = 0)
    {
        var standard = Standards.FirstOrDefault(x => string.Equals(x.Name, paperName.Replace("+", ""), StringComparison.OrdinalIgnoreCase));
        if (standard == null)
        {
            return (Math.Max(currentWidth, 1), Math.Max(currentHeight, 1));
        }

        var landscape = currentWidth <= 0 || currentHeight <= 0 || currentWidth >= currentHeight;
        if (!paperName.EndsWith("+", StringComparison.OrdinalIgnoreCase))
        {
            return landscape ? (standard.LongSide, standard.ShortSide) : (standard.ShortSide, standard.LongSide);
        }

        var measuredLong = Math.Max(currentWidth, currentHeight);
        var longSide = measuredLong > standard.LongSide
            ? SnapLongSide(measuredLong, standard.LongSide)
            : standard.LongSide * 1.25;
        return landscape ? (longSide, standard.ShortSide) : (standard.ShortSide, longSide);
    }

    private static void AddCandidate(List<PaperCandidate> candidates, StandardPaper paper, double scale, double actualWidth, double actualHeight)
    {
        var widthMm = actualWidth / scale;
        var heightMm = actualHeight / scale;

        AddOrientation(candidates, paper, scale, widthMm, heightMm, actualWidth, actualHeight, paper.LongSide, paper.ShortSide);
        AddOrientation(candidates, paper, scale, widthMm, heightMm, actualWidth, actualHeight, paper.ShortSide, paper.LongSide);
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
        double standardHeight)
    {
        var widthError = RelativeError(widthMm, standardWidth);
        var heightError = RelativeError(heightMm, standardHeight);
        if (widthError <= 0.04 && heightError <= 0.04)
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
        if (!isLong || shortError > 0.04)
        {
            return;
        }

        var snappedLong = SnapLongSide(actualLong, expectedLong);
        var longError = RelativeError(actualLong, snappedLong);
        if (longError > 0.08)
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
            Reason = $"短边锁定 {expectedShort:0.##}mm，长边按标准长边的 1/4 倍数匹配为 {snappedLong:0.##}mm，短边误差 {shortError:P1}，长边误差 {longError:P1}，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
        });
    }

    private static double SnapLongSide(double measuredLong, double standardLong)
    {
        var quarters = Math.Max(5, (int)Math.Round(measuredLong / standardLong * 4, MidpointRounding.AwayFromZero));
        return standardLong * quarters / 4d;
    }

    private static PaperDetection FallbackDetect(double actualWidth, double actualHeight)
    {
        var shortSide = Math.Min(actualWidth, actualHeight);
        var longSide = Math.Max(actualWidth, actualHeight);
        return new PaperDetection
        {
            Note = $"未匹配到 A0/A1/A2/A3，实际尺寸 {longSide:0.##} x {shortSide:0.##}"
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
