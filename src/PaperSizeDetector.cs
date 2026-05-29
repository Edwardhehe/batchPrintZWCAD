using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZwcadBatchPlot;

public static class PaperSizeDetector
{
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

    public static PaperDetection Detect(double width, double height)
    {
        var shortSide = Math.Min(Math.Abs(width), Math.Abs(height));
        var longSide = Math.Max(Math.Abs(width), Math.Abs(height));
        var candidates = new List<(StandardPaper Paper, double Scale, double Error, bool IsLong)>();

        foreach (var paper in Standards)
        {
            var scaleFromShort = shortSide / paper.ShortSide;
            if (scaleFromShort <= 0)
            {
                continue;
            }

            var expectedLong = paper.LongSide * scaleFromShort;
            var longError = Math.Abs(longSide - expectedLong) / Math.Max(expectedLong, 1);
            var isLong = longSide > expectedLong * 1.08;

            // For elongated drawings, the short side is the anchor and the long side may grow
            // by 1/4, 1/2, or any project-specific extension.
            var error = isLong
                ? Math.Abs(RoundScale(scaleFromShort) - scaleFromShort) / Math.Max(scaleFromShort, 1)
                : longError;

            candidates.Add((paper, scaleFromShort, error, isLong));
        }

        var best = candidates
            .Where(x => x.Error < 0.12)
            .OrderBy(x => x.Error)
            .ThenBy(x => Array.IndexOf(Standards, x.Paper))
            .FirstOrDefault();

        if (best.Paper == null)
        {
            return new PaperDetection
            {
                Note = $"未匹配到 A0/A1/A2/A3，实际尺寸 {longSide:0.##} x {shortSide:0.##}"
            };
        }

        var roundedScale = RoundScale(best.Scale);
        var paperName = best.IsLong ? best.Paper.Name + "+" : best.Paper.Name;

        return new PaperDetection
        {
            PaperName = paperName,
            ScaleValue = roundedScale,
            ScaleText = ToScaleText(roundedScale),
            IsLong = best.IsLong,
            Note = best.IsLong
                ? $"{best.Paper.Name} 加长，短边匹配，长边超过标准长边"
                : $"{best.Paper.Name} 标准图幅"
        };
    }

    private static double RoundScale(double value)
    {
        if (value < 1)
        {
            return Math.Round(value, 3);
        }

        var common = new[] { 1d, 2d, 5d, 10d, 20d, 25d, 50d, 100d, 150d, 200d, 250d, 500d, 1000d };
        var nearest = common.OrderBy(x => Math.Abs(x - value)).First();
        if (Math.Abs(nearest - value) / nearest < 0.08)
        {
            return nearest;
        }

        return Math.Round(value);
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
