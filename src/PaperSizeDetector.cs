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
        new StandardPaper("A0", 841, 1189),
        new StandardPaper("A1", 594, 841),
        new StandardPaper("A2", 420, 594),
        new StandardPaper("A3", 297, 420)
    };

    private static readonly double[] CommonScales =
    {
        1d, 2d, 5d, 10d, 20d, 25d, 50d, 75d, 100d, 125d, 150d, 200d, 250d, 500d, 1000d
    };

    public static PaperDetection Detect(double width, double height)
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

        var best = candidates
            .Where(x => x.Score < 0.18)
            .OrderBy(x => x.Score)
            .ThenByDescending(x => x.Scale)
            .ThenBy(x => Array.IndexOf(Standards, x.Paper))
            .FirstOrDefault();

        if (best == null)
        {
            return FallbackDetect(actualWidth, actualHeight);
        }

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

    private static void AddCandidate(List<PaperCandidate> candidates, StandardPaper paper, double scale, double actualWidth, double actualHeight)
    {
        var widthMm = actualWidth / scale;
        var heightMm = actualHeight / scale;

        var landscape = ScoreStandard(widthMm, heightMm, paper.LongSide, paper.ShortSide);
        var portrait = ScoreStandard(widthMm, heightMm, paper.ShortSide, paper.LongSide);
        var standard = landscape.Score <= portrait.Score
            ? (landscape.Score, Width: paper.LongSide, Height: paper.ShortSide, landscape.Reason)
            : (portrait.Score, Width: paper.ShortSide, Height: paper.LongSide, portrait.Reason);

        if (standard.Score < 0.18)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = standard.Score,
                IsLong = false,
                PaperWidthMm = standard.Width,
                PaperHeightMm = standard.Height,
                Reason = $"{standard.Reason}，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
            });
        }

        var longPaper = ScoreLong(widthMm, heightMm, paper);
        if (longPaper.Score < 0.08)
        {
            candidates.Add(new PaperCandidate
            {
                Paper = paper,
                Scale = scale,
                Score = longPaper.Score + 0.01,
                IsLong = true,
                PaperWidthMm = longPaper.Width,
                PaperHeightMm = longPaper.Height,
                Reason = $"{longPaper.Reason}，CAD尺寸 {actualWidth:0.##} x {actualHeight:0.##}"
            });
        }
    }

    private static (double Score, string Reason) ScoreStandard(double widthMm, double heightMm, double targetWidth, double targetHeight)
    {
        var widthError = RelativeError(widthMm, targetWidth);
        var heightError = RelativeError(heightMm, targetHeight);

        // A real drawing block may include text or attributes outside the paper border.
        // If one side hits a standard paper side exactly, let that side anchor the paper.
        var anchorError = Math.Min(widthError, heightError);
        var otherError = Math.Max(widthError, heightError);
        var score = anchorError * 0.72 + otherError * 0.28;

        if (anchorError < 0.025 && otherError < 0.16)
        {
            score *= 0.45;
        }

        return (score, $"按常用比例匹配，宽误差 {widthError:P1}，高误差 {heightError:P1}");
    }

    private static (double Score, double Width, double Height, string Reason) ScoreLong(double widthMm, double heightMm, StandardPaper paper)
    {
        var widthLandscape = ScoreLongOrientation(widthMm, heightMm, paper.LongSide, paper.ShortSide);
        var widthPortrait = ScoreLongOrientation(widthMm, heightMm, paper.ShortSide, paper.LongSide);
        return widthLandscape.Score <= widthPortrait.Score ? widthLandscape : widthPortrait;
    }

    private static (double Score, double Width, double Height, string Reason) ScoreLongOrientation(double widthMm, double heightMm, double standardLongAxis, double standardShortAxis)
    {
        var shortAxis = Math.Min(widthMm, heightMm);
        var longAxis = Math.Max(widthMm, heightMm);
        var shortError = RelativeError(shortAxis, standardShortAxis);
        var isLong = longAxis > standardLongAxis * 1.08;
        if (!isLong || shortError > 0.035)
        {
            return (1, 0, 0, "");
        }

        var landscape = widthMm >= heightMm;
        var outputWidth = landscape ? longAxis : standardShortAxis;
        var outputHeight = landscape ? standardShortAxis : longAxis;
        return (shortError, outputWidth, outputHeight, $"短边匹配加长图，短边误差 {shortError:P1}");
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

        return scale.ToString("0.###", CultureInfo.InvariantCulture) + ":1";
    }
}
