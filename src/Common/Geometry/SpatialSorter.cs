using System;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 图纸空间排序工具 — 用锚点带状分组 + 矩形边沿重叠判断实现行列排序，
/// 图框块和矩形框批量打印共用同一算法，保证排序结果一致。
/// </summary>
public static class SpatialSorter
{
    /// <summary>
    /// 按 CAD 布局 TabOrder 分组后做纯位置排序。布局之间不比较坐标，布局内部调用统一的
    /// <see cref="Sort"/> 行列算法；图框块与矩形框批打共同使用，保证多布局顺序也一致。
    /// </summary>
    public static List<PlotJob> SortByLayout(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        var result = new List<PlotJob>(jobs.Count);
        var layoutGroups = jobs
            .Select((job, index) => new { Job = job, Index = index })
            // CAD 布局名不区分大小写；防止外部 DWG/旧任务中的名称大小写差异把同一布局拆开。
            .GroupBy(item => item.Job.SpaceName ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Min(item => item.Job.LayoutTabOrder))
            // 兼容旧任务：没有 TabOrder 时，保持扫描/传入列表中的布局先后。
            .ThenBy(group => group.Min(item => item.Index))
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var layoutGroup in layoutGroups)
        {
            result.AddRange(Sort(
                layoutGroup.Select(item => item.Job).ToList(),
                horizontalFirst));
        }

        return result;
    }

    /// <summary>按空间位置排序，支持"从上到下、从左到右"和"从左到右、从上到下"两种方向。</summary>
    /// <param name="jobs">待排序的图纸任务列表</param>
    /// <param name="horizontalFirst">true=从左到右、从上到下；false=从上到下、从左到右</param>
    public static List<PlotJob> Sort(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        if (jobs.Count <= 1)
        {
            return jobs.ToList();
        }

        var remaining = jobs
            .Select((job, index) => new SortItem(job, index))
            .ToList();
        var result = new List<PlotJob>(jobs.Count);

        // 每次从阅读方向最前端选一个锚点，只把与“锚点本身”重叠的图框收入当前行/列。
        // 禁止使用并查集的传递闭包：较高的大图框可能同时碰到上下两行，A~B、B~C 的
        // 传递关系会把本来分开的 A、C 错并为一行，正是混合 A1/A3 图框顺序跳动的根因。
        while (remaining.Count > 0)
        {
            SortItem anchor;
            List<SortItem> band;
            if (horizontalFirst)
            {
                // 从左到右选列；同列内从上到下。
                anchor = remaining
                    .OrderBy(item => MinX(item.Job))
                    .ThenByDescending(item => MaxY(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .First();
                band = remaining
                    .Where(item => SharesBandWithAnchor(anchor.Job, item.Job, horizontalFirst: true))
                    .OrderByDescending(item => CenterY(item.Job))
                    .ThenBy(item => CenterX(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .ToList();
            }
            else
            {
                // 从上到下选行；同行内从左到右。
                anchor = remaining
                    .OrderByDescending(item => MaxY(item.Job))
                    .ThenBy(item => MinX(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .First();
                band = remaining
                    .Where(item => SharesBandWithAnchor(anchor.Job, item.Job, horizontalFirst: false))
                    .OrderBy(item => CenterX(item.Job))
                    .ThenByDescending(item => CenterY(item.Job))
                    .ThenBy(item => item.OriginalIndex)
                    .ToList();
            }

            result.AddRange(band.Select(item => item.Job));
            var selectedIndices = new HashSet<int>(band.Select(item => item.OriginalIndex));
            remaining.RemoveAll(item => selectedIndices.Contains(item.OriginalIndex));
        }

        return result;
    }

    private static bool SharesBandWithAnchor(PlotJob anchor, PlotJob candidate, bool horizontalFirst)
    {
        if (ReferenceEquals(anchor, candidate))
        {
            return true;
        }

        if (horizontalFirst)
        {
            var overlap = Math.Min(MaxX(anchor), MaxX(candidate)) - Math.Max(MinX(anchor), MinX(candidate));
            var smallerWidth = Math.Min(MaxX(anchor) - MinX(anchor), MaxX(candidate) - MinX(candidate));
            return smallerWidth > 1e-6 && overlap >= smallerWidth * 0.3d;
        }

        var verticalOverlap = Math.Min(MaxY(anchor), MaxY(candidate)) - Math.Max(MinY(anchor), MinY(candidate));
        var smallerHeight = Math.Min(MaxY(anchor) - MinY(anchor), MaxY(candidate) - MinY(candidate));
        return smallerHeight > 1e-6 && verticalOverlap >= smallerHeight * 0.3d;
    }

    private sealed class SortItem
    {
        public SortItem(PlotJob job, int originalIndex)
        {
            Job = job;
            OriginalIndex = originalIndex;
        }

        public PlotJob Job { get; }
        public int OriginalIndex { get; }
    }

    // UCS 任务必须在 UCS 坐标内排序；若退回 WCS 包围盒，旋转后行列会因包围盒重叠而错组。
    private static double MinX(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMinX : job.MinX;
    private static double MinY(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMinY : job.MinY;
    private static double MaxX(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMaxX : job.MaxX;
    private static double MaxY(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMaxY : job.MaxY;
    private static double CenterX(PlotJob job) => (MinX(job) + MaxX(job)) / 2d;
    private static double CenterY(PlotJob job) => (MinY(job) + MaxY(job)) / 2d;
}
