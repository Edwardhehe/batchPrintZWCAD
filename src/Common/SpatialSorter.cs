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
                    var overlapX = Math.Min(ri.MaxX, rj.MaxX) - Math.Max(ri.MinX, rj.MinX);
                    var minW = Math.Min(ri.MaxX - ri.MinX, rj.MaxX - rj.MinX);
                    if (overlapX >= minW * 0.3) Union(i, j);
                }
                else
                {
                    // 行分组：Y 区间重叠
                    var overlapY = Math.Min(ri.MaxY, rj.MaxY) - Math.Max(ri.MinY, rj.MinY);
                    var minH = Math.Min(ri.MaxY - ri.MinY, rj.MaxY - rj.MinY);
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

    private static double CenterX(PlotJob job) => (job.MinX + job.MaxX) / 2d;
    private static double CenterY(PlotJob job) => (job.MinY + job.MaxY) / 2d;
}
