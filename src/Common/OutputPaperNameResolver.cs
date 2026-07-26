using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZwcadBatchPlot;

/// <summary>
/// 计算文件名、图纸目录、CSV 等输出内容使用的图幅名。
/// 此类只改变“输出时如何命名”，绝不能回写 PlotJob 的实际纸张名称或物理尺寸。
/// </summary>
public static class OutputPaperNameResolver
{
    private static readonly IReadOnlyDictionary<string, double> StandardLongSides =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["A0"] = 1189d,
            ["A1"] = 841d,
            ["A2"] = 594d,
            ["A3"] = 420d,
            ["A4"] = 297d
        };

    public static string Resolve(PlotJob job, double longSideToleranceMm)
    {
        if (job == null)
        {
            return "";
        }

        return Resolve(
            job.PaperName,
            job.PaperWidthMm,
            job.PaperHeightMm,
            longSideToleranceMm);
    }

    /// <summary>
    /// 已识别为加长图时，将实测物理长边吸附到标准长边的最近 1/8 整数倍。
    /// 只有长边误差不超过输出专用容差才改变输出图幅名；实际打印仍保留原始尺寸。
    /// </summary>
    public static string Resolve(
        string? actualPaperName,
        double paperWidthMm,
        double paperHeightMm,
        double longSideToleranceMm)
    {
        var originalName = actualPaperName?.Trim() ?? "";
        var plusIndex = originalName.IndexOf('+');
        if (plusIndex <= 0)
        {
            // 普通 A0～A4 以及未知/自定义名称不属于“输出加长图”规则。
            return originalName;
        }

        var baseName = originalName.Substring(0, plusIndex).Trim();
        if (!StandardLongSides.TryGetValue(baseName, out var standardLongSide))
        {
            return originalName;
        }

        var measuredLongSide = Math.Max(Math.Abs(paperWidthMm), Math.Abs(paperHeightMm));
        if (measuredLongSide <= standardLongSide || longSideToleranceMm < 0)
        {
            return originalName;
        }

        // 总长以标准长边的 1/8 为步长；例如 A1+1.5 表示总长为 A1 长边的 2.5 倍。
        var totalUnits8 = Math.Max(
            9,
            (int)Math.Round(
                measuredLongSide / standardLongSide * 8d,
                MidpointRounding.AwayFromZero));
        var snappedLongSide = standardLongSide * totalUnits8 / 8d;
        var longSideErrorMm = Math.Abs(measuredLongSide - snappedLongSide);
        if (longSideErrorMm > longSideToleranceMm)
        {
            return originalName;
        }

        var extension = (totalUnits8 - 8) / 8d;
        return baseName + "+" + extension.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
