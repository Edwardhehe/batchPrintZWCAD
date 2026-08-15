using System;
using System.Collections.Generic;
using System.Linq;

namespace ZwcadBatchPlot;

/// <summary>
/// 图纸空间排序工具 — 用并查集 + 矩形边沿重叠判断实现行列分组，
/// 图号重排和矩形框批量打印共用同一算法，保证排序结果一致。
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
            .GroupBy(item => item.Job.SpaceName ?? "", StringComparer.Ordinal)
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

        // ── 并查集分组：矩形边沿重叠 ≥ 较小边长的 30% → 同一行/列 ──
        // 相比旧版中心点+中位数间隙法，此方法对不同大小的图框（如 A0 和 A3 同行）也能正确分组。
        var parent = Enumerable.Range(0, jobs.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        for (var i = 0; i < jobs.Count; i++)
        {
            for (var j = i + 1; j < jobs.Count; j++)
            {
                var ri = jobs[i];
                var rj = jobs[j];
                if (horizontalFirst)
                {
                    // 列分组：X 区间重叠
                    var overlapX = Math.Min(MaxX(ri), MaxX(rj)) - Math.Max(MinX(ri), MinX(rj));
                    var minW = Math.Min(MaxX(ri) - MinX(ri), MaxX(rj) - MinX(rj));
                    if (overlapX >= minW * 0.3) Union(i, j);
                }
                else
                {
                    // 行分组：Y 区间重叠
                    var overlapY = Math.Min(MaxY(ri), MaxY(rj)) - Math.Max(MinY(ri), MinY(rj));
                    var minH = Math.Min(MaxY(ri) - MinY(ri), MaxY(rj) - MinY(rj));
                    if (overlapY >= minH * 0.3) Union(i, j);
                }
            }
        }

        // ── 按分组整理 ──
        var groupMap = new Dictionary<int, List<PlotJob>>();
        for (var i = 0; i < jobs.Count; i++)
        {
            var root = Find(i);
            if (!groupMap.TryGetValue(root, out var list))
            {
                list = new List<PlotJob>();
                groupMap[root] = list;
            }
            list.Add(jobs[i]);
        }

        var groups = groupMap.Values.ToList();

        // ── 组内排序 ──
        foreach (var group in groups)
        {
            if (horizontalFirst)
                group.Sort((a, b) => CenterY(b).CompareTo(CenterY(a))); // 列内 Y 降序（从上到下）
            else
                group.Sort((a, b) => CenterX(a).CompareTo(CenterX(b))); // 行内 X 升序（从左到右）
        }

        // ── 组间排序 ──
        if (horizontalFirst)
            groups = groups.OrderBy(g => g.Average(r => CenterX(r))).ToList();  // 列按 X 升序
        else
            groups = groups.OrderByDescending(g => g.Average(r => CenterY(r))).ToList(); // 行按 Y 降序

        // ── 展平 ──
        var result = new List<PlotJob>();
        foreach (var group in groups) result.AddRange(group);
        return result;
    }

    // UCS 任务必须在 UCS 坐标内排序；若退回 WCS 包围盒，旋转后行列会因包围盒重叠而错组。
    private static double MinX(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMinX : job.MinX;
    private static double MinY(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMinY : job.MinY;
    private static double MaxX(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMaxX : job.MaxX;
    private static double MaxY(PlotJob job) => job.UsesUserCoordinateSystem ? job.UcsMaxY : job.MaxY;
    private static double CenterX(PlotJob job) => (MinX(job) + MaxX(job)) / 2d;
    private static double CenterY(PlotJob job) => (MinY(job) + MaxY(job)) / 2d;
}
