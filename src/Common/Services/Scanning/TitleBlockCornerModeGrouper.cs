using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 把图框库按纸张 1:1 后的图名、图号角点归类。
/// 角点是相对图纸右下角的纸面毫米：向左、向上为正。
/// 相差不超过 <see cref="ToleranceMm"/> 的图框视为同一模式，因此同一套图框的 A2、A3 会归在一起。
/// </summary>
public static class TitleBlockCornerModeGrouper
{
    /// <summary>纸面毫米容差。四个角点边距都在此范围内才算同一模式。</summary>
    public const double ToleranceMm = 1;

    /// <summary>
    /// 按块名顺序聚类。模式编号按成员数量从多到少。
    /// </summary>
    public static TitleBlockCornerModeResult Group(IEnumerable<TitleBlockDefinition> blocks)
    {
        var measured = new List<MeasuredBlock>();
        var unmeasured = new List<string>();
        foreach (var block in (blocks ?? Enumerable.Empty<TitleBlockDefinition>())
            .Where(x => x != null)
            .OrderBy(x => x.BlockName ?? "", StringComparer.CurrentCultureIgnoreCase))
        {
            if (TryMeasure(block, out var title, out var number))
            {
                measured.Add(new MeasuredBlock(block, title, number));
            }
            else
            {
                unmeasured.Add(block.BlockName ?? "");
            }
        }

        var clusters = new List<List<MeasuredBlock>>();
        foreach (var item in measured)
        {
            var hit = clusters.Find(cluster => SameMode(cluster[0], item));
            if (hit == null)
            {
                clusters.Add(new List<MeasuredBlock> { item });
            }
            else
            {
                hit.Add(item);
            }
        }

        var modes = clusters
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x[0].Block.BlockName ?? "", StringComparer.CurrentCultureIgnoreCase)
            .Select((cluster, index) => new TitleBlockCornerMode(
                index + 1,
                cluster[0].Title,
                cluster[0].Number,
                cluster.Select(x => x.Block.BlockName ?? "").ToList(),
                cluster.Select(x => x.Block.PaperName ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        return new TitleBlockCornerModeResult(modes, unmeasured);
    }

    /// <summary>
    /// 用打印边界短边对齐纸张短边，把图名、图号矩形换成相对图纸右下角的纸面毫米。
    /// </summary>
    public static bool TryMeasure(TitleBlockDefinition block, out PaperCornerBox title, out PaperCornerBox number)
    {
        title = default;
        number = default;
        if (block == null || block.PrintRegion == null || block.TitleRegion == null || block.DrawingNumberRegion == null)
        {
            return false;
        }

        if (!block.PrintRegion.HasArea() || !block.TitleRegion.HasArea() || !block.DrawingNumberRegion.HasArea())
        {
            return false;
        }

        var frameWidth = Math.Abs(block.PrintRegion.MaxX - block.PrintRegion.MinX);
        var frameHeight = Math.Abs(block.PrintRegion.MaxY - block.PrintRegion.MinY);
        var paperWidth = Math.Abs(block.PaperWidthMm);
        var paperHeight = Math.Abs(block.PaperHeightMm);
        if (frameWidth < 1e-6 || frameHeight < 1e-6 || paperWidth < 1e-6 || paperHeight < 1e-6)
        {
            return false;
        }

        var mmPerCad = Math.Min(paperWidth, paperHeight) / Math.Min(frameWidth, frameHeight);
        var mode = block.CoordinateMode ?? "";
        return TryBox(block.TitleRegion, mode, frameWidth, block.PrintRegion, mmPerCad, out title)
            && TryBox(block.DrawingNumberRegion, mode, frameWidth, block.PrintRegion, mmPerCad, out number);
    }

    private static bool SameMode(MeasuredBlock left, MeasuredBlock right)
    {
        return Close(left.Title, right.Title) && Close(left.Number, right.Number);
    }

    private static bool Close(PaperCornerBox left, PaperCornerBox right)
    {
        return Math.Abs(left.RightMm - right.RightMm) <= ToleranceMm
            && Math.Abs(left.LeftMm - right.LeftMm) <= ToleranceMm
            && Math.Abs(left.BottomMm - right.BottomMm) <= ToleranceMm
            && Math.Abs(left.TopMm - right.TopMm) <= ToleranceMm;
    }

    private static bool TryBox(
        LocalRectangle region,
        string mode,
        double frameWidth,
        LocalRectangle print,
        double mmPerCad,
        out PaperCornerBox box)
    {
        double rightA;
        double rightB;
        double bottom;
        double top;
        if (string.Equals(mode, TitleBlockDefinition.DynamicRightBottomCoordinateMode, StringComparison.OrdinalIgnoreCase))
        {
            rightA = -region.MaxX;
            rightB = -region.MinX;
            bottom = Math.Min(region.MinY, region.MaxY);
            top = Math.Max(region.MinY, region.MaxY);
        }
        else if (string.Equals(mode, "Frame", StringComparison.OrdinalIgnoreCase))
        {
            rightA = frameWidth - region.MaxX;
            rightB = frameWidth - region.MinX;
            bottom = Math.Min(region.MinY, region.MaxY);
            top = Math.Max(region.MinY, region.MaxY);
        }
        else
        {
            rightA = print.MaxX - region.MaxX;
            rightB = print.MaxX - region.MinX;
            bottom = Math.Min(region.MinY, region.MaxY) - print.MinY;
            top = Math.Max(region.MinY, region.MaxY) - print.MinY;
        }

        box = new PaperCornerBox(
            Math.Min(rightA, rightB) * mmPerCad,
            Math.Max(rightA, rightB) * mmPerCad,
            Math.Min(bottom, top) * mmPerCad,
            Math.Max(bottom, top) * mmPerCad);
        return true;
    }

    private readonly struct MeasuredBlock
    {
        public MeasuredBlock(TitleBlockDefinition block, PaperCornerBox title, PaperCornerBox number)
        {
            Block = block;
            Title = title;
            Number = number;
        }

        public TitleBlockDefinition Block { get; }
        public PaperCornerBox Title { get; }
        public PaperCornerBox Number { get; }
    }
}

/// <summary>图名或图号矩形相对图纸右下角的纸面范围，单位毫米。</summary>
public readonly struct PaperCornerBox
{
    public PaperCornerBox(double rightMm, double leftMm, double bottomMm, double topMm)
    {
        RightMm = rightMm;
        LeftMm = leftMm;
        BottomMm = bottomMm;
        TopMm = topMm;
    }

    /// <summary>靠近右边缘的那条竖边，距图纸右边的毫米。</summary>
    public double RightMm { get; }

    /// <summary>远离右边缘的那条竖边，距图纸右边的毫米。</summary>
    public double LeftMm { get; }

    /// <summary>下边距图纸下边的毫米。</summary>
    public double BottomMm { get; }

    /// <summary>上边距图纸下边的毫米。</summary>
    public double TopMm { get; }

    /// <summary>右下角、左上角两个角点，足以定出矩形。</summary>
    public string Describe()
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "右下({0:0.#},{1:0.#}) 左上({2:0.#},{3:0.#})",
            RightMm,
            BottomMm,
            LeftMm,
            TopMm);
    }
}

/// <summary>同一套右下角关系的图框。</summary>
public sealed class TitleBlockCornerMode
{
    public TitleBlockCornerMode(
        int index,
        PaperCornerBox title,
        PaperCornerBox number,
        IReadOnlyList<string> blockNames,
        IReadOnlyList<string> paperNames)
    {
        Index = index;
        Title = title;
        Number = number;
        BlockNames = blockNames ?? Array.Empty<string>();
        PaperNames = paperNames ?? Array.Empty<string>();
    }

    public int Index { get; }

    public string Label => "模式" + Index.ToString(CultureInfo.InvariantCulture);

    public PaperCornerBox Title { get; }

    public PaperCornerBox Number { get; }

    public IReadOnlyList<string> BlockNames { get; }

    public IReadOnlyList<string> PaperNames { get; }
}

/// <summary>一次识别的全部分组。</summary>
public sealed class TitleBlockCornerModeResult
{
    public TitleBlockCornerModeResult(IReadOnlyList<TitleBlockCornerMode> modes, IReadOnlyList<string> unmeasuredBlockNames)
    {
        Modes = modes ?? Array.Empty<TitleBlockCornerMode>();
        UnmeasuredBlockNames = unmeasuredBlockNames ?? Array.Empty<string>();
    }

    public IReadOnlyList<TitleBlockCornerMode> Modes { get; }

    public IReadOnlyList<string> UnmeasuredBlockNames { get; }
}
