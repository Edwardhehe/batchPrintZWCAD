using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>一次三点图签识别结果：命中的图框 + 使用的库条目 + 取到的字段值。</summary>
public sealed class ThreePointMatch
{
    public RectangleFrameScanner.Result? Frame { get; set; }
    public ThreePointDefinition Definition { get; set; } = new();
    public Dictionary<string, string> Values { get; set; } = new();
    public double Score { get; set; }
    public Point3d[] AnchorPoints { get; set; } = Array.Empty<Point3d>();
}

/// <summary>
/// 一次扫描的匹配统计，用来把「为什么没识别到」讲清楚：
/// 是框内没文字、没锚点文字，还是三点几何对不上。
/// </summary>
public sealed class ThreePointScanDiagnostics
{
    public int Frames { get; set; }
    /// <summary>图框范围内一个字都没取到。</summary>
    public int PoolEmpty { get; set; }
    /// <summary>框内没找到那三个锚点文字（图签可能在框外，或锚点文字被改过）。</summary>
    public int AnchorTextMissing { get; set; }
    /// <summary>锚点文字找到了，但三点几何/相似度和录入时对不上。</summary>
    public int GeometryRejected { get; set; }
    public int Matched { get; set; }
    /// <summary>靠「整图兜底」命中的个数。</summary>
    public int FallbackMatched { get; set; }
    public List<string> Notes { get; } = new List<string>();

    public string Summary()
    {
        var parts = new List<string>();
        if (PoolEmpty > 0) parts.Add($"{PoolEmpty} 个框内没有任何文字");
        if (AnchorTextMissing > 0) parts.Add($"{AnchorTextMissing} 个框内找不到三个锚点文字");
        if (GeometryRejected > 0) parts.Add($"{GeometryRejected} 个锚点找到了但三点几何对不上");
        if (FallbackMatched > 0) parts.Add($"其中 {FallbackMatched} 个按整图兜底命中");
        parts.AddRange(Notes);
        return string.Join("；", parts);
    }
}

/// <summary>
/// 三点文字识别图签：在已识别出的矩形图框内找三个锚点文字，解仿射后把字段区域映射过去取字。
/// 打印范围始终来自矩形图框检测；仿射只负责字段定位，二者解耦，
/// 这样图框被拉长、旋转、镜像时字段仍然对得上。
/// </summary>
public static class ThreePointTitleScanner
{
    /// <summary>
    /// 对已识别的矩形图框逐个匹配图签。未命中任何条目的图框不会进入清单（由调用方统计 skipped）。
    /// </summary>
    public static List<ThreePointMatch> MatchFrames(
        Document document,
        IReadOnlyList<RectangleFrameScanner.Result> frames,
        ThreePointLibrary library,
        out int skippedFrames)
    {
        return MatchFrames(document, frames, library, out skippedFrames, out _);
    }

    /// <summary>
    /// 同上，额外输出诊断计数，供界面把「为什么没识别到」讲清楚。
    /// <para>
    /// 只有当前文档的文字可用，等同于「所有图框都来自当前 DWG」；
    /// 多文档批打请用带 <see cref="ThreePointPoolProvider"/> 的重载，按来源文件各取一份文字。
    /// </para>
    /// </summary>
    public static List<ThreePointMatch> MatchFrames(
        Document document,
        IReadOnlyList<RectangleFrameScanner.Result> frames,
        ThreePointLibrary library,
        out int skippedFrames,
        out ThreePointScanDiagnostics diagnostics)
    {
        return MatchFrames(
            (_, spaceName) =>
            {
                using var tr = document.Database.TransactionManager.StartTransaction();
                var samples = ThreePointTextCollector.CollectSpace(document.Database, tr, spaceName);
                tr.Commit();
                return samples;
            },
            frames,
            library,
            out skippedFrames,
            out diagnostics);
    }

    /// <summary>
    /// 三点图签的文字来源：来源 DWG + 空间名 → 该空间里的文字。
    /// 多文档批打时每个图框用自己的 DWG 文字，不能一律拿当前文档的。
    /// </summary>
    internal delegate List<ThreePointTextSample> ThreePointPoolProvider(string sourceFile, string spaceName);

    /// <summary>
    /// 同上，文字池由调用方按「文件 + 空间」提供（多文档批打用：每个 DWG 各收一份）。
    /// </summary>
    internal static List<ThreePointMatch> MatchFrames(
        ThreePointPoolProvider poolProvider,
        IReadOnlyList<RectangleFrameScanner.Result> frames,
        ThreePointLibrary library,
        out int skippedFrames,
        out ThreePointScanDiagnostics diagnostics)
    {
        skippedFrames = 0;
        diagnostics = new ThreePointScanDiagnostics { Frames = frames.Count };
        // 排序优先级小的先试；同优先级按名称稳定排序，保证每次扫描结果一致。
        var definitions = library.Items
            .Where(x => x.Enabled && x.IsComplete)
            .OrderBy(x => x.Priority <= 0 ? 1 : x.Priority)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (definitions.Count == 0 || frames.Count == 0)
        {
            skippedFrames = frames.Count;
            diagnostics.Notes.Add(frames.Count == 0 ? "没有图框" : "没有可用的图签定义");
            ThreePointScanLog.Write($"match aborted: frames={frames.Count}, definitions={definitions.Count}");
            return new List<ThreePointMatch>();
        }

        // 清单可能跨多个 DWG、模型空间和多个布局：按「文件 + 空间」分别收文字，同一组合只收一次。
        // 多文档时这一步是关键——拿当前文档的文字去匹配别的 DWG 的图框，
        // 坐标撞上就串图、撞不上就整框判空。
        var poolsBySpace = new Dictionary<string, List<ThreePointTextSample>>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in frames)
        {
            var key = PoolKeyOf(frame.Job);
            if (!poolsBySpace.ContainsKey(key))
            {
                poolsBySpace[key] = poolProvider?.Invoke(frame.Job.SourceFile ?? "", frame.Job.SpaceName ?? "")
                    ?? new List<ThreePointTextSample>();
            }
        }

        ThreePointScanLog.Write(
            $"match start: frames={frames.Count}, definitions={definitions.Count}, "
            + "pools=" + string.Join(", ", poolsBySpace.Select(x => $"[{x.Key}]={x.Value.Count}")));

        // 把「空间里到底有没有那三个锚点文字、各有几个」记下来，
        // 这样日志里能直接看出是锚点文字对不上，还是图框范围没盖住图签。
        foreach (var space in poolsBySpace)
        {
            var parts = new List<string>();
            foreach (var definition in definitions)
            {
                foreach (var anchor in definition.Anchors)
                {
                    var key = ThreePointTextCollector.Normalize(anchor.Text);
                    var hits = space.Value.Count(x => x.CanAnchor
                        && string.Equals(x.Text, key, StringComparison.OrdinalIgnoreCase));
                    parts.Add($"{definition.Name}「{key}」={hits}");
                }
            }

            ThreePointScanLog.Write($"[{PoolLabelOf(space.Key)}] anchor hits: {string.Join(", ", parts)}");
        }

        var matches = new List<ThreePointMatch>();
        var spaceCandidateCache = new Dictionary<string, List<AnchorCandidate>>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in frames)
        {
            var spaceKey = PoolKeyOf(frame.Job);
            if (!poolsBySpace.TryGetValue(spaceKey, out var samples))
            {
                samples = new List<ThreePointTextSample>();
                poolsBySpace[spaceKey] = samples;
            }

            var localPool = CollectFramePool(samples, frame.Job);
            var frameBox = BoxOf(frame.Job);
            // 日志里把 WCS 图框范围一起写出来：字段取不到时能一眼看出字段区域是不是在框外。
            string label = $"[{PoolLabelOf(spaceKey)}] {frame.Job.MinX:0},{frame.Job.MinY:0} "
                + $"box={frameBox.MinX:0},{frameBox.MinY:0}-{frameBox.MaxX:0},{frameBox.MaxY:0}";
            if (localPool.Count == 0)
            {
                diagnostics.PoolEmpty++;
                ThreePointScanLog.Write($"{label}: frame pool empty (space samples={samples.Count})");
            }

            // ① 先用「图框包围盒内」的文字做锚点：快，而且天然不会和隔壁图框抢图签。
            //    字段取值统一用整空间文字池（仿射已经把格子映射到本框，不会串框），
            //    这样图名/图号压在框线上或略微出框也能取到。
            var inFrameCandidates = BuildCandidates(definitions, localPool, MaxCandidatesPerDefinition);
            var best = PickBest(inFrameCandidates, samples, frame, label, out var chosen);
            var source = "frame";

            // ② 框内配不上（图签在图框外、加长图框、或框内文字太少）→ 用整空间的锚点组，
            //    按「三个锚点都离本框足够近」分配。一组锚点只给一个框，
            //    避免先处理的框把隔壁框的图签抢走（这正是「有的图有、有的没有」的来源）。
            if (best == null)
            {
                if (!spaceCandidateCache.TryGetValue(spaceKey, out var spaceCandidates))
                {
                    spaceCandidates = BuildCandidates(definitions, samples, MaxSpaceCandidatesPerDefinition);
                    spaceCandidateCache[spaceKey] = spaceCandidates;
                }

                var gate = Math.Max(frameBox.Diagonal * 0.2, 1.0);
                var nearby = spaceCandidates
                    .Where(x => !x.Used && BoxDistance(frameBox, x) <= gate)
                    .ToList();
                best = PickBest(nearby, samples, frame, label, out chosen);
                if (best != null)
                {
                    source = "space-fallback";
                    diagnostics.FallbackMatched++;
                }
                else if (spaceCandidates.Count > 0)
                {
                    ThreePointScanLog.Write(
                        $"{label}: space candidates={spaceCandidates.Count}, in gate={nearby.Count}, gate={gate:0}");
                }
            }

            if (best == null)
            {
                if (localPool.Count > 0)
                {
                    if (HasAllAnchorText(definitions, localPool))
                    {
                        diagnostics.GeometryRejected++;
                    }
                    else
                    {
                        diagnostics.AnchorTextMissing++;
                    }
                }

                skippedFrames++;
                ThreePointScanLog.Write(
                    $"{label}: skipped, pool={localPool.Count}, space={samples.Count}, "
                    + $"inFrameCandidates={inFrameCandidates.Count}");
                continue;
            }

            // 选中的锚点组标记为已用：同一套图签不能再分配给第二张图。
            var chosenCandidate = chosen!;
            chosenCandidate.Used = true;

            diagnostics.Matched++;
            var fieldText = best.Values.Count == 0
                ? "（字段范围没取到文字）"
                : string.Join(", ", best.Values.Select(x => $"{x.Key}={x.Value}"));
            // 共线锚点只能解相似变换，日志里标出来，免得排查时误以为是完整仿射。
            var fitMode = chosenCandidate.Fit.Collinear
                ? $"similar(共线锚点, 残差={chosenCandidate.Fit.CollinearResidual:0.####})"
                : "affine";
            ThreePointScanLog.Write(
                $"{label}: matched by {source}, definition={best.Definition.Name}, "
                + $"score={best.Score:0.####}, fit={fitMode}, pool={localPool.Count}, fields={fieldText}");
            matches.Add(best);
        }

        ThreePointScanLog.Write(
            $"match done: matched={diagnostics.Matched}, skipped={skippedFrames}, "
            + $"poolEmpty={diagnostics.PoolEmpty}, noAnchorText={diagnostics.AnchorTextMissing}, "
            + $"geometryRejected={diagnostics.GeometryRejected}, fallback={diagnostics.FallbackMatched}");
        return matches;
    }

    /// <summary>框内池每次最多留几组候选锚点：够覆盖「同一图框里锚点重复出现」，又不至于算太慢。</summary>
    private const int MaxCandidatesPerDefinition = 8;

    /// <summary>整空间兜底时每个定义最多留几组候选（多张图各一套图签，要留够）。</summary>
    private const int MaxSpaceCandidatesPerDefinition = 120;

    /// <summary>
    /// 共线锚点的残差上限。共线时只有两个点真正参与定标，第三个点只是校验，
    /// 判别信息比三角形少得多——三点间距比例差一点，字段格子就会整体错位，
    /// 所以不再沿用「按相似容差放宽到 8%」的口径，一律收到 3% 以内。
    /// </summary>
    private const double CollinearToleranceLimit = 0.03;

    /// <summary>一组成立的锚点三元组（还没算字段值）。</summary>
    private sealed class AnchorCandidate
    {
        public ThreePointDefinition Definition { get; set; } = new();
        public Point3d[] Anchors { get; set; } = Array.Empty<Point3d>();
        public ThreePointAffine.FitResult Fit { get; set; } = new();
        /// <summary>相似性误差（越小越像同一套图签）。</summary>
        public double Error { get; set; }
        /// <summary>已经被某张图用掉了，不能再分配给别的图框。</summary>
        public bool Used { get; set; }
    }

    /// <summary>
    /// 枚举池子里所有成立的锚点三元组，每个定义按误差从小到大留前 <paramref name="maxPerDefinition"/> 组。
    /// <para>
    /// 之所以要「留多组」而不是直接取误差最小的一组：一张图上同一套图签会重复出现，
    /// 误差略大的那一组有时才是真正对齐本框的（对齐好的那组取不到字段）。
    /// 现在把候选交给 <see cref="PickBest"/> 用「实际取到几个字段」来定胜负。
    /// </para>
    /// </summary>
    private static List<AnchorCandidate> BuildCandidates(
        IReadOnlyList<ThreePointDefinition> definitions,
        IReadOnlyList<ThreePointTextSample> pool,
        int maxPerDefinition)
    {
        var results = new List<AnchorCandidate>();
        if (pool.Count == 0)
        {
            return results;
        }

        foreach (var definition in definitions)
        {
            if (definition.Anchors.Count != 3)
            {
                continue;
            }

            var firsts = pool.Where(x => x.CanAnchor
                && string.Equals(x.Text, ThreePointTextCollector.Normalize(definition.Anchors[0].Text), StringComparison.OrdinalIgnoreCase)).ToList();
            var seconds = pool.Where(x => x.CanAnchor
                && string.Equals(x.Text, ThreePointTextCollector.Normalize(definition.Anchors[1].Text), StringComparison.OrdinalIgnoreCase)).ToList();
            var thirds = pool.Where(x => x.CanAnchor
                && string.Equals(x.Text, ThreePointTextCollector.Normalize(definition.Anchors[2].Text), StringComparison.OrdinalIgnoreCase)).ToList();
            if (firsts.Count == 0 || seconds.Count == 0 || thirds.Count == 0)
            {
                continue;
            }

            var local = new List<AnchorCandidate>();
            // 组合上限：锚点文字在这张图里重复很多次时（例如好几个图框都写着“图 号”），
            // 三重循环会退化成 次³，可能算上几分钟甚至把内存吃光。
            var maxCombos = Math.Max(150, 8000000 / Math.Max(1, pool.Count * Math.Max(1, definition.Fields.Count)));
            var combos = 0;
            foreach (var first in firsts)
            {
                if (combos >= maxCombos)
                {
                    break;
                }

                foreach (var second in seconds)
                {
                    if (combos >= maxCombos)
                    {
                        break;
                    }

                    if (IsSame(first, second)) continue;
                    foreach (var third in thirds)
                    {
                        if (combos++ >= maxCombos)
                        {
                            break;
                        }

                        if (IsSame(first, third) || IsSame(second, third)) continue;
                        if (!TryBuild(definition, first.Point, second.Point, third.Point, out var fit, out var error))
                        {
                            continue;
                        }

                        local.Add(new AnchorCandidate
                        {
                            Definition = definition,
                            Anchors = new[] { first.Point, second.Point, third.Point },
                            Fit = fit,
                            Error = error
                        });
                    }
                }
            }

            if (local.Count > maxPerDefinition)
            {
                local.Sort((a, b) => a.Error.CompareTo(b.Error));
                local.RemoveRange(maxPerDefinition, local.Count - maxPerDefinition);
            }

            results.AddRange(local);
        }

        return results;
    }

    /// <summary>
    /// 在候选锚点组里挑最终结果：**先用字段取值结果定胜负，误差只作次要依据**。
    /// <para>
    /// 早先是「先按几何误差选一组锚点，再回头取字段」，一旦选中的那组整体偏几个单位，
    /// 字段格子就全部落空，用户看到的是「图框匹配上了但图名空着」。
    /// 现在对每组候选都真算一遍字段，取到字段多的赢；同字段数再比误差。
    /// 同时优先只认可信候选（误差 ≤ 容差 60%），只有在可信候选全部取不到字段时，
    /// 才退回边缘候选，避免凑巧对上的三点把别的图的文字取过来。
    /// </para>
    /// </summary>
    private static ThreePointMatch? PickBest(
        IReadOnlyList<AnchorCandidate> candidates,
        IReadOnlyList<ThreePointTextSample> spaceSamples,
        RectangleFrameScanner.Result frame,
        string label,
        out AnchorCandidate? chosen)
    {
        chosen = null;
        if (candidates.Count == 0)
        {
            return null;
        }

        ThreePointMatch? best = null;
        var bestFilled = -1;
        var bestError = double.MaxValue;
        var bestQualified = false;
        List<string>? bestNotes = null;

        foreach (var candidate in candidates)
        {
            if (candidate.Used)
            {
                continue;
            }

            var notes = new List<string>();
            var values = ExtractFields(candidate.Definition, candidate.Fit, spaceSamples, notes);
            var filled = values.Count(x => !string.IsNullOrWhiteSpace(x.Value));
            var limit = Math.Max(0.5, candidate.Definition.SimilarityTolerancePercent) / 100.0;
            var qualified = candidate.Error <= limit * 0.6 + 1e-9;

            var better = best == null
                || qualified && !bestQualified
                || qualified == bestQualified
                   && (filled > bestFilled || filled == bestFilled && candidate.Error < bestError - 1e-9);
            if (!better)
            {
                continue;
            }

            bestFilled = filled;
            bestError = candidate.Error;
            bestQualified = qualified;
            bestNotes = notes;
            chosen = candidate;
            best = new ThreePointMatch
            {
                Frame = frame,
                Definition = candidate.Definition,
                Values = values,
                Score = candidate.Error,
                AnchorPoints = candidate.Anchors
            };
        }

        if (bestNotes != null && bestNotes.Count > 0)
        {
            ThreePointScanLog.Write($"  field refine({label}): {string.Join("; ", bestNotes)}");
        }

        return best;
    }

    /// <summary>候选锚点三点到图框的最大距离，用作「这套图签是不是属于这个框」的门槛。</summary>
    private static double BoxDistance(FrameBox box, AnchorCandidate candidate)
    {
        var distance = 0.0;
        foreach (var anchor in candidate.Anchors)
        {
            distance = Math.Max(distance, box.DistanceTo(anchor));
        }

        return distance;
    }

    /// <summary>池子里是否三个锚点文字各至少出现一次（用来区分「没文字」和「文字有但几何对不上」）。</summary>
    private static bool HasAllAnchorText(
        IReadOnlyList<ThreePointDefinition> definitions,
        IReadOnlyList<ThreePointTextSample> pool)
    {
        foreach (var definition in definitions)
        {
            if (definition.Anchors.Count != 3)
            {
                continue;
            }

            var complete = true;
            foreach (var anchor in definition.Anchors)
            {
                var key = ThreePointTextCollector.Normalize(anchor.Text);
                if (!pool.Any(x => x.CanAnchor && string.Equals(x.Text, key, StringComparison.OrdinalIgnoreCase)))
                {
                    complete = false;
                    break;
                }
            }

            if (complete)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 试识别：不依赖矩形图框，直接在当前空间的全部文字里找锚点三元组。
    /// 仅用于入库后的自检（能认出几处、字段取到什么值）。
    /// </summary>
    public static List<ThreePointMatch> Probe(Document document, ThreePointDefinition definition)
    {
        if (!definition.IsComplete)
        {
            return new List<ThreePointMatch>();
        }

        List<ThreePointTextSample> samples;
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            samples = ThreePointTextCollector.CollectCurrentSpace(document, tr);
            tr.Commit();
        }

        var results = new List<ThreePointMatch>();
        var used = new HashSet<int>();
        // 大图里“日期/图号”这类标签可能出现几十次，每个都当一次起点会把试识别拖到几分钟。
        var seeds = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            if (seeds >= 300 || results.Count >= 200)
            {
                break;
            }

            if (used.Contains(i) || !samples[i].CanAnchor)
            {
                continue;
            }

            seeds++;
            var pool = samples;
            if (!TryMatchFrom(definition, pool, samples[i], out var values, out var error, out var anchors))
            {
                continue;
            }

            results.Add(new ThreePointMatch
            {
                Definition = definition,
                Values = values,
                Score = error,
                AnchorPoints = anchors
            });

            foreach (var anchor in anchors)
            {
                for (var j = 0; j < samples.Count; j++)
                {
                    if (samples[j].Point.DistanceTo(anchor) < 1e-6)
                    {
                        used.Add(j);
                    }
                }
            }
        }

        return results;
    }

    public static void Apply(ThreePointMatch match)
    {
        var job = match.Frame?.Job;
        if (job == null)
        {
            return;
        }

        job.DetectionNote = "三点图签: " + match.Definition.Name;
        foreach (var pair in match.Values)
        {
            switch (pair.Key)
            {
                case ThreePointFieldKeys.Title:
                    job.Title = pair.Value;
                    job.CadTitle = pair.Value;
                    break;
                case ThreePointFieldKeys.DrawingNumber:
                    job.DrawingNumber = pair.Value;
                    job.CadDrawingNumber = pair.Value;
                    break;
                case ThreePointFieldKeys.Scale:
                    job.DrawingScale = pair.Value;
                    break;
                case ThreePointFieldKeys.SheetSize:
                    job.SheetSize = pair.Value;
                    break;
                case ThreePointFieldKeys.ProjectName:
                    job.ProjectName = pair.Value;
                    break;
                case ThreePointFieldKeys.Date:
                    job.Date = pair.Value;
                    job.CadDate = pair.Value;
                    break;
                case ThreePointFieldKeys.Revision:
                    job.Revision = pair.Value;
                    job.CadRevision = pair.Value;
                    break;
                case ThreePointFieldKeys.Phase:
                    job.Phase = pair.Value;
                    job.CadPhase = pair.Value;
                    break;
                case ThreePointFieldKeys.Info1:
                    job.Info1 = pair.Value;
                    job.CadInfo1 = pair.Value;
                    break;
                case ThreePointFieldKeys.Info2:
                    job.Info2 = pair.Value;
                    job.CadInfo2 = pair.Value;
                    break;
            }
        }
    }

    /// <summary>
    /// 取图框在图纸上的 WCS 范围。
    /// 扫描后调用方会做 WCS→DCS 变换，Job 的 Min/Max 变成打印坐标；
    /// 「直接识别清单里已有的图纸」时若还拿 DCS 范围去筛文字（文字是 WCS 坐标），
    /// 整框文字都会被筛掉，表现为所有图框一律跳过。所以优先用扫描阶段留存的 WCS 范围。
    /// </summary>
    private static void FrameExtents(PlotJob job, out double minX, out double minY, out double maxX, out double maxY)
    {
        if (job.ScanExtentsCaptured)
        {
            minX = job.ScanMinX;
            minY = job.ScanMinY;
            maxX = job.ScanMaxX;
            maxY = job.ScanMaxY;
            return;
        }

        minX = job.MinX;
        minY = job.MinY;
        maxX = job.MaxX;
        maxY = job.MaxY;
    }

    private static List<ThreePointTextSample> CollectFramePool(
        IReadOnlyList<ThreePointTextSample> samples,
        PlotJob job)
    {
        FrameExtents(job, out var rawMinX, out var rawMinY, out var rawMaxX, out var rawMaxY);
        var width = Math.Abs(rawMaxX - rawMinX);
        var height = Math.Abs(rawMaxY - rawMinY);
        var pad = Math.Max(1.0, Math.Max(width, height) * 0.02);
        var minX = Math.Min(rawMinX, rawMaxX) - pad;
        var maxX = Math.Max(rawMinX, rawMaxX) + pad;
        var minY = Math.Min(rawMinY, rawMaxY) - pad;
        var maxY = Math.Max(rawMinY, rawMaxY) + pad;

        return samples.Where(x =>
            x.Point.X >= minX && x.Point.X <= maxX &&
            x.Point.Y >= minY && x.Point.Y <= maxY).ToList();
    }

    /// <summary>图框的 WCS 包围盒，用于判断一组锚点离哪个框最近。</summary>
    private readonly struct FrameBox
    {
        public FrameBox(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }

        public double Diagonal
        {
            get
            {
                var w = Math.Max(0, MaxX - MinX);
                var h = Math.Max(0, MaxY - MinY);
                return Math.Sqrt(w * w + h * h);
            }
        }

        /// <summary>点到盒子的最短距离，盒内为 0。</summary>
        public double DistanceTo(Point3d point)
        {
            var dx = point.X < MinX ? MinX - point.X : point.X > MaxX ? point.X - MaxX : 0;
            var dy = point.Y < MinY ? MinY - point.Y : point.Y > MaxY ? point.Y - MaxY : 0;
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }

    private static FrameBox BoxOf(PlotJob job)
    {
        FrameExtents(job, out var minX, out var minY, out var maxX, out var maxY);
        return new FrameBox(Math.Min(minX, maxX), Math.Min(minY, maxY), Math.Max(minX, maxX), Math.Max(minY, maxY));
    }

    /// <summary>
    /// 文字池的键：来源 DWG 全路径 + 空间名。
    /// 多文档批打时清单里混着多个 DWG 的图框，同一张图里同名布局很常见，
    /// 只按空间名做键会把不同文件的文字混成一池（既是串图，也是「整框判空」的来源）。
    /// </summary>
    private static string PoolKeyOf(PlotJob job)
    {
        var file = job.SourceFile ?? "";
        if (file.Length > 0)
        {
            try
            {
                file = Path.GetFullPath(file);
            }
            catch
            {
                // 路径异常（已删除、非法字符）时保留原样，日志里仍能看出是哪份文件。
            }
        }

        return file + "|" + (job.SpaceName ?? "");
    }

    /// <summary>日志用的短键：只留文件名，避免整条日志被长路径撑满。</summary>
    private static string PoolLabelOf(string key)
    {
        var index = key.LastIndexOf('|');
        if (index < 0)
        {
            return key;
        }

        var file = key.Substring(0, index);
        var space = key.Substring(index + 1);
        if (file.Length == 0)
        {
            return "当前文档|" + space;
        }

        try
        {
            return Path.GetFileName(file) + "|" + space;
        }
        catch
        {
            return key;
        }
    }

    private static bool TryMatchFrom(
        ThreePointDefinition definition,
        IReadOnlyList<ThreePointTextSample> pool,
        ThreePointTextSample seed,
        out Dictionary<string, string> values,
        out double error,
        out Point3d[] anchors)
    {
        values = new Dictionary<string, string>();
        error = double.MaxValue;
        anchors = Array.Empty<Point3d>();

        if (definition.Anchors.Count != 3
            || !string.Equals(seed.Text, ThreePointTextCollector.Normalize(definition.Anchors[0].Text), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidates = pool
            .Where(x => x.CanAnchor
                && string.Equals(x.Text, ThreePointTextCollector.Normalize(definition.Anchors[1].Text), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var thirdCandidates = pool
            .Where(x => x.CanAnchor
                && string.Equals(x.Text, ThreePointTextCollector.Normalize(definition.Anchors[2].Text), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var bestError = double.MaxValue;
        var bestValues = new Dictionary<string, string>();
        var bestAnchors = Array.Empty<Point3d>();

        // 同 MatchAnchors：给三重循环加组合上限，防止密集图纸把这里算到卡死。
        var maxCombos = Math.Max(150, 8000000 / Math.Max(1, pool.Count * Math.Max(1, definition.Fields.Count)));
        var combos = 0;
        foreach (var second in candidates)
        {
            if (combos >= maxCombos)
            {
                break;
            }

            if (IsSame(seed, second))
            {
                continue;
            }

            foreach (var third in thirdCandidates)
            {
                if (combos++ >= maxCombos)
                {
                    break;
                }

                if (IsSame(seed, third) || IsSame(second, third))
                {
                    continue;
                }

                if (!TryBuild(definition, seed.Point, second.Point, third.Point, out var fit, out var currentError))
                {
                    continue;
                }

                // 三点定位成功：按该变换取字段，再用“能取到几个非空字段”做加权，
                // 避免同一图框里多套重复文字时选中错误的一组。
                var extracted = ExtractFields(definition, fit, pool);
                var filled = extracted.Count(x => !string.IsNullOrWhiteSpace(x.Value));
                var adjusted = currentError - filled * 0.002;
                if (adjusted >= bestError)
                {
                    continue;
                }

                bestError = adjusted;
                bestValues = extracted;
                bestAnchors = new[] { seed.Point, second.Point, third.Point };
            }
        }

        if (bestError == double.MaxValue)
        {
            return false;
        }

        values = bestValues;
        error = bestError;
        anchors = bestAnchors;
        return true;
    }

    private static bool TryBuild(
        ThreePointDefinition definition,
        Point3d p0,
        Point3d p1,
        Point3d p2,
        out ThreePointAffine.FitResult fit,
        out double error)
    {
        error = double.MaxValue;
        var a0 = new Point3d(definition.Anchors[0].X, definition.Anchors[0].Y, 0);
        var a1 = new Point3d(definition.Anchors[1].X, definition.Anchors[1].Y, 0);
        var a2 = new Point3d(definition.Anchors[2].X, definition.Anchors[2].Y, 0);

        if (!ThreePointAffine.TryFit(a0, a1, a2, p0, p1, p2, out fit))
        {
            return false;
        }

        if (fit.Mirrored && !definition.AllowMirror)
        {
            return false;
        }

        if (fit.Scale < 1e-6)
        {
            return false;
        }

        var limit = Math.Max(0.5, definition.SimilarityTolerancePercent) / 100.0;
        // 共线锚点收紧容差，理由见 CollinearToleranceLimit 的说明。
        if (fit.Collinear)
        {
            limit = Math.Min(limit, CollinearToleranceLimit);
        }

        if (fit.SimilarityError > limit)
        {
            return false;
        }

        error = fit.SimilarityError;
        return true;
    }

    private static Dictionary<string, string> ExtractFields(
        ThreePointDefinition definition,
        ThreePointAffine.FitResult fit,
        IReadOnlyList<ThreePointTextSample> pool)
    {
        return ExtractFields(definition, fit, pool, null);
    }

    private static Dictionary<string, string> ExtractFields(
        ThreePointDefinition definition,
        ThreePointAffine.FitResult fit,
        IReadOnlyList<ThreePointTextSample> pool,
        List<string>? notes)
    {
        var values = new Dictionary<string, string>();
        var regions = new List<FieldRegion>();
        foreach (var pair in definition.Fields)
        {
            if (pair.Value == null || !pair.Value.HasArea())
            {
                continue;
            }

            var corners = ThreePointAffine.TransformCorners(fit, pair.Value);
            regions.Add(new FieldRegion(pair.Key, ThreePointFieldKeys.Display(pair.Key), corners));
        }

        if (regions.Count == 0)
        {
            return values;
        }

        if (pool.Count == 0)
        {
            notes?.Add("（该空间没有可用文字）");
            return values;
        }

        // 容差上限：不超过区域真实短边的 35%，也不超过与其它字段间距的 40%。
        // 早先逐级放宽到对角线的 40%，结果「图号」的格子把上面「图名」整行都吞了进去，
        // 用户看到的就是字段内容串位、几张图的文字混在一起。
        // 注意 gap 恰好为 0 的情况：图名格子与图号格子相贴时 gap=0，按 gap*0.4 会把容差压成 0，
        // 于是「图号」永远是空（日志里那串「容差 0.5」就是这么来的）。相贴时改用自身短边的 35%。
        // <para>
        // 这里绝不能再加「绝对上限 2.5」：图纸单位可能是 mm、图签尺寸几千单位，
        // 2.5 只相当于短边的 0.15%。实测用户图纸「防爆波活门门框墙配筋表」字高 500，
        // 左下插入点只越出图名格子边界 9.59 单位就被判空（图号却正常取到），
        // 而按下面这条 35% 规则算出的容差是 595，本该轻松命中。
        // </para>
        foreach (var region in regions)
        {
            var gap = double.MaxValue;
            foreach (var other in regions)
            {
                if (ReferenceEquals(other, region))
                {
                    continue;
                }

                gap = Math.Min(gap, region.BoxGap(other));
            }

            var own = 0.35 * region.ShortSide;
            var limit = own;
            if (!double.IsPositiveInfinity(gap) && gap > 1e-6)
            {
                limit = Math.Min(own, gap * 0.4);
            }

            // 下限 0.5 只照顾「字段格子只有几个单位」的小图签；上限不再用绝对值砍掉。
            region.Guard = Math.Max(0.5, limit);
        }

        // 预筛：字段格子只占图签的一小块，整空间上万段文字逐段做多边形判定太慢。
        // 先按「图签整体包围盒 + 允许范围」粗筛，一次把远处文字排掉（对结果没有影响）。
        var unionMinX = regions.Min(r => r.MinX);
        var unionMinY = regions.Min(r => r.MinY);
        var unionMaxX = regions.Max(r => r.MaxX);
        var unionMaxY = regions.Max(r => r.MaxY);
        var unionWidth = Math.Max(0, unionMaxX - unionMinX);
        var unionHeight = Math.Max(0, unionMaxY - unionMinY);
        // 预筛半径：只决定「哪些文字进入候选池」，绝不作为补位的允许距离。
        // 补位距离一律用字段自身的 Guard（真实短边 35%），见第二轮。
        // 早先把这个半径（图签对角线的一半）直接当补位距离，结果把邻格的阶段、
        // 日期、图幅文字都硬填进空字段（实测 d=1859.6 / 14710.8 / 22535.8）。
        var poolReach = Math.Max(6.0, Math.Sqrt(unionWidth * unionWidth + unionHeight * unionHeight) * 0.5);
        var slack = Math.Max(poolReach, regions.Max(r => r.Guard)) + 1.0;

        bool WithinUnion(Point3d point)
        {
            return point.X >= unionMinX - slack && point.X <= unionMaxX + slack
                && point.Y >= unionMinY - slack && point.Y <= unionMaxY + slack;
        }

        var fastPool = new List<ThreePointTextSample>(pool.Count);
        foreach (var sample in pool)
        {
            if (WithinUnion(sample.Point) || sample.AltPoint.HasValue && WithinUnion(sample.AltPoint.Value))
            {
                fastPool.Add(sample);
            }
        }

        if (fastPool.Count == 0)
        {
            notes?.Add(
                $"（图签附近没有文字，区域 {unionMinX:0},{unionMinY:0}-{unionMaxX:0},{unionMaxY:0}，"
                + $"整空间 {pool.Count} 段）");
            return values;
        }

        // 第一轮：严格落在格子里的文字。同一段文字同时落在多个格子时归给面积最小的那个，
        // 保证一段文字只属于一个字段（否则图名/图号会互相包含对方的字）。
        // 图签上的固定标签（「图 号」「比 例」以及三个锚点文字）不是字段值，一律跳过，
        // 否则格子刚好框住标签时图名栏会填出「图 号」这种垃圾值。
        var claimed = new HashSet<ThreePointTextSample>();
        foreach (var sample in fastPool)
        {
            if (IsLabelText(definition, sample.Text))
            {
                continue;
            }
            FieldRegion? owner = null;
            foreach (var region in regions)
            {
                if (!region.Hit(sample, 0))
                {
                    continue;
                }

                if (owner == null || region.Area < owner.Area)
                {
                    owner = region;
                }
            }

            if (owner == null)
            {
                continue;
            }

            owner.Samples.Add(sample);
            claimed.Add(sample);
        }

        // 第二轮：仍空着的字段，在「按真实短边算出的容差」里就近捡还没被认领的文字。
        // 早先是把容差内的文字全部塞进格子，容差按尺度放大后会把附近标签一起拼进图名；
        // 改成按到真实四边形的距离排序、只取最近的 1~2 段，同距离时优先字高大的
        // （图名往往比标签大得多），并跳过落在别的格子里的文字。
        foreach (var region in regions)
        {
            if (region.Samples.Count > 0)
            {
                continue;
            }

            var pairs = CollectRefillCandidates(region, regions, fastPool, claimed, definition, region.Guard);
            SortRefillCandidates(pairs);
            foreach (var pair in pairs)
            {
                if (claimed.Contains(pair.Sample) || pair.Region.Samples.Count >= 2)
                {
                    break;
                }

                pair.Region.Samples.Add(pair.Sample);
                claimed.Add(pair.Sample);
                notes?.Add($"{pair.Region.Label} 容差命中「{pair.Sample.Text}」d={pair.Distance:0.#}");
            }
        }

        // 不再做「超出 Guard 的就近补位」。
        // 早先第三轮按图签外接框对角线的一半取字，本意是救「图签整体偏移」，
        // 但它无法区分「本字段的字偏了一点」和「隔壁格子的阶段/日期/图幅」：
        // 现场日志里空格子被填成 图名=施工图设计(d=1859.6)、
        // 图号=2018.8(d=14710.8)+A2(d=22535.8)，而那张图的图名格子里本来就没有文字。
        // 真正需要的「插入点压线、字大越界」已由第二轮按字段真实短边算出的 Guard 命中
        // （1.dwg 的「防爆波活门门框墙配筋表」就是这样救回来的，d=0）。
        // 空字段应保持为空，下方会列出最近 3 段候选及距离供人工复核。

        foreach (var region in regions)
        {
            if (region.Samples.Count == 0)
            {
                // 日志里给出最近的几段候选，下次再漏识别时能直接看出文字到底偏到哪去了。
                var nearest = fastPool
                    .Where(x => !claimed.Contains(x) && !IsLabelText(definition, x.Text))
                    .Select(x => new { Text = x.Text, Distance = region.DistanceTo(x) })
                    .OrderBy(x => x.Distance)
                    .Take(3)
                    .Select(x => $"「{x.Text}」d={x.Distance:0.#}");
                notes?.Add(
                    $"{region.Label} 空（区域 {region.BoxText()} 短边 {region.ShortSide:0.#} 容差 {region.Guard:0.#}，"
                    + $"最近：{string.Join("、", nearest)}）");
                continue;
            }

            var text = ComposeRegionText(region.Samples);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            values[region.Key] = text;
            notes?.Add($"{region.Label} {region.Samples.Count}段「{text}」");
        }

        return values;
    }

    /// <summary>一个字段在目标图纸上的格子（已按三点仿射映射），以及它能吃的文字。</summary>
    private sealed class FieldRegion
    {
        public FieldRegion(string key, string label, Point3d[] corners)
        {
            Key = key;
            Label = label;
            Corners = corners;
            MinX = corners.Min(p => p.X);
            MinY = corners.Min(p => p.Y);
            MaxX = corners.Max(p => p.X);
            MaxY = corners.Max(p => p.Y);
            // 旋转 UCS 下框出的字段是斜四边形：包围盒的宽高比真实格子大好几倍，
            // 容差必须按真实边长算，不能用包围盒。
            var side01 = corners[0].DistanceTo(corners[1]);
            var side12 = corners[1].DistanceTo(corners[2]);
            ShortSide = Math.Min(side01, side12);
            LongSide = Math.Max(side01, side12);
        }

        public string Key { get; }
        public string Label { get; }
        public Point3d[] Corners { get; }
        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        /// <summary>斜四边形真实短边（不是包围盒），字段容差按它计算。</summary>
        public double ShortSide { get; }
        public double LongSide { get; }
        public double Area => Math.Max(0, Width) * Math.Max(0, Height);
        public double Guard { get; set; }
        public List<ThreePointTextSample> Samples { get; } = new();

        /// <summary>文字的两个候选位置任一落在格子里（可按 tolerance 放宽）即算命中。</summary>
        public bool Hit(ThreePointTextSample sample, double tolerance)
        {
            if (ThreePointAffine.PolygonContains(Corners, sample.Point, tolerance))
            {
                return true;
            }

            return sample.AltPoint.HasValue
                && ThreePointAffine.PolygonContains(Corners, sample.AltPoint.Value, tolerance);
        }

        /// <summary>两个格子包围盒之间的最短距离（相交为 0）。</summary>
        public double BoxGap(FieldRegion other)
        {
            var dx = Math.Max(0, Math.Max(MinX - other.MaxX, other.MinX - MaxX));
            var dy = Math.Max(0, Math.Max(MinY - other.MaxY, other.MinY - MaxY));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// 文字（插入点 / 对齐点 / 几何代表点）到真实四边形的最短距离，落在格子里为 0。
        /// 早先用包围盒距离：旋转 UCS 的斜格子包围盒比真实格子大好几倍，
        /// 会把明显在格子外的文字算成距离 0，就近补位时容易串字段。
        /// </summary>
        public double DistanceTo(ThreePointTextSample sample)
        {
            var distance = DistanceToPolygon(sample.Point);
            if (sample.AltPoint.HasValue)
            {
                distance = Math.Min(distance, DistanceToPolygon(sample.AltPoint.Value));
            }

            if (sample.ExtraPoints != null)
            {
                foreach (var point in sample.ExtraPoints)
                {
                    distance = Math.Min(distance, DistanceToPolygon(point));
                    if (distance <= 0)
                    {
                        break;
                    }
                }
            }

            return distance;
        }

        private double DistanceToPolygon(Point3d point)
        {
            if (ThreePointAffine.PolygonContains(Corners, point, 0))
            {
                return 0;
            }

            var best = double.MaxValue;
            for (var i = 0; i < Corners.Length; i++)
            {
                var a = Corners[i];
                var b = Corners[(i + 1) % Corners.Length];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var length2 = dx * dx + dy * dy;
                var t = length2 > 1e-12
                    ? Math.Max(0, Math.Min(1, ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length2))
                    : 0;
                var px = a.X + t * dx - point.X;
                var py = a.Y + t * dy - point.Y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }

            return best;
        }

        public string BoxText()
        {
            return $"{MinX:0},{MinY:0}-{MaxX:0},{MaxY:0}";
        }
    }

    /// <summary>
    /// 为某个仍空着的格子收集可补位的文字：跳过已被认领的、图签固定标签、
    /// 明显不是字段值的元数据（阶段/日期/图幅/比例），
    /// 以及严格落在其它格子里的文字（那是别的字段的内容）。
    /// </summary>
    private static List<FieldRefill> CollectRefillCandidates(
        FieldRegion region,
        IReadOnlyList<FieldRegion> regions,
        IReadOnlyList<ThreePointTextSample> pool,
        HashSet<ThreePointTextSample> claimed,
        ThreePointDefinition definition,
        double reach)
    {
        var pairs = new List<FieldRefill>();
        foreach (var sample in pool)
        {
            if (claimed.Contains(sample) || IsLabelText(definition, sample.Text)
                || IsNonFieldValue(sample.Text))
            {
                continue;
            }

            var insideOther = false;
            foreach (var other in regions)
            {
                if (!ReferenceEquals(other, region) && other.Hit(sample, 0))
                {
                    insideOther = true;
                    break;
                }
            }

            if (insideOther)
            {
                continue;
            }

            var distance = region.DistanceTo(sample);
            if (distance <= reach)
            {
                pairs.Add(new FieldRefill(region, sample, distance));
            }
        }

        return pairs;
    }

    /// <summary>距离近的优先；距离相同时字高大的优先（图名通常明显大于图签标签）。</summary>
    private static void SortRefillCandidates(List<FieldRefill> pairs)
    {
        pairs.Sort((a, b) =>
        {
            var byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0)
            {
                return byDistance;
            }

            var byHeight = b.Sample.Height.CompareTo(a.Sample.Height);
            return byHeight != 0 ? byHeight : string.CompareOrdinal(a.Sample.Text, b.Sample.Text);
        });
    }

    /// <summary>「空格子 → 就近文字」的一次候选配对（按距离升序贪心认领）。</summary>
    private readonly struct FieldRefill
    {
        public FieldRefill(FieldRegion region, ThreePointTextSample sample, double distance)
        {
            Region = region;
            Sample = sample;
            Distance = distance;
        }

        public FieldRegion Region { get; }
        public ThreePointTextSample Sample { get; }
        public double Distance { get; }
    }

    /// <summary>
    /// 图签上的固定标签（不是字段值）。
    /// 就近补位时如果把这些标签当成内容，图号栏会填出「图 号」「比 例」这种垃圾值。
    /// </summary>
    private static readonly string[] FieldLabelTexts =
    {
        "图名", "图号", "比例", "日期", "版次", "阶段", "图幅", "工程名称", "项目名称",
        "设计", "制图", "审核", "校核", "会签", "批准", "专业", "签名", "建设单位",
        "设计单位", "专业负责人", "项目编号", "工程编号", "电子文档", "图纸名称",
        "单位名称", "子项", "备注", "共", "第", "张"
    };

    /// <summary>标签被拆成单字时的碎片（图签里的「图」「名」竖排两行），不是字段值。</summary>
    private const string SingleCharacterLabels = "图名号称期例次段幅计核审准建单位项目编日专业签共第张备注阶设子";

    private static bool IsLabelText(ThreePointDefinition definition, string text)
    {
        foreach (var anchor in definition.Anchors)
        {
            if (string.Equals(ThreePointTextCollector.Normalize(anchor.Text), text, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 「图号/电子文档：」「图 名」「单 位 名 称」这类标签带分隔符和冒号，
        // 早先「长度 > 6 一律放行」让它们被就近补位当成字段值（图号栏出现过「图 图号/电子文档：」）。
        var bare = new string(text
            .Where(ch => !char.IsWhiteSpace(ch) && !IsLabelPunctuation(ch))
            .ToArray());
        if (bare.Length == 0)
        {
            return true;
        }

        // 真正的图名/图号可能就是十几个字，超过这个长度一律当内容，不再猜标签。
        if (bare.Length > 14)
        {
            return false;
        }

        foreach (var label in FieldLabelTexts)
        {
            if (string.Equals(label, bare, StringComparison.Ordinal))
            {
                return true;
            }

            // 「图号电子文档」「项目编号A」：以标签开头、后面只是短后缀。
            if (label.Length >= 2
                && bare.Length > label.Length
                && bare.Length <= label.Length + 6
                && bare.StartsWith(label, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return bare.Length == 1 && SingleCharacterLabels.IndexOf(bare[0]) >= 0;
    }

    /// <summary>
    /// 明显不是图名/图号内容的元数据：设计阶段、日期、比例、图幅代号、纯数字碎片。
    /// 只在「空字段补位」时使用；第一轮严格落在格子里的文字不受影响
    /// （有些图纸的图号本来就是纯数字）。
    /// 现场日志里这些值曾被就近补位填进空字段：施工图设计、2018.8、A2、65。
    /// </summary>
    private static bool IsNonFieldValue(string text)
    {
        var bare = new string(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (bare.Length == 0)
        {
            return true;
        }

        // 阶段/设计类短语：施工图设计、方案设计、初步设计、施工图阶段、竣工图…
        if (bare.Length <= 8
            && (bare.EndsWith("设计", StringComparison.Ordinal)
                || bare.EndsWith("阶段", StringComparison.Ordinal)
                || bare.EndsWith("施工图", StringComparison.Ordinal)
                || bare.EndsWith("竣工图", StringComparison.Ordinal)
                || bare == "方案" || bare == "初设" || bare == "扩初"))
        {
            return true;
        }

        // 图幅代号 A0~A9 / B4（只认两字符，避免误伤 A23 这类图号）。
        if (bare.Length == 2 && char.IsLetter(bare[0]) && char.IsDigit(bare[1]))
        {
            return true;
        }

        // 日期 2018.8 / 2026-11、比例 1:100、纯数字碎片 65：
        // 去掉分隔符后全是数字，就不是图名内容。
        var compact = new string(bare
            .Where(ch => !IsLabelPunctuation(ch) && ch != ':' && ch != '：'
                && ch != '年' && ch != '月' && ch != '日')
            .ToArray());
        return compact.Length > 0 && compact.All(char.IsDigit);
    }

    private static bool IsLabelPunctuation(char ch)
    {
        return ch == '/' || ch == ':' || ch == '：' || ch == '、' || ch == ',' || ch == '，'
            || ch == '.' || ch == '。' || ch == '-' || ch == '_' || ch == '(' || ch == ')'
            || ch == '（' || ch == '）' || ch == ';' || ch == '；' || ch == '"' || ch == '\''
            || ch == '\\' || ch == '[' || ch == ']' || ch == '【' || ch == '】';
    }

    /// <summary>
    /// 区域内可能有多段文字：以字高最大的为基准，把同一数量级的字按“上→下、左→右”拼起来；
    /// 同一行直接相连，不同行用空格分隔。
    /// </summary>
    private static string ComposeRegionText(IReadOnlyList<ThreePointTextSample> samples)
    {
        var maxHeight = samples.Max(x => x.Height);
        var chosen = maxHeight > 1e-6
            ? samples.Where(x => x.Height <= 0 || x.Height >= maxHeight * 0.55).ToList()
            : samples.ToList();

        var ordered = chosen
            .OrderByDescending(x => x.Point.Y)
            .ThenBy(x => x.Point.X)
            .ToList();

        var builder = new System.Text.StringBuilder();
        ThreePointTextSample? previous = null;
        foreach (var sample in ordered)
        {
            if (previous != null)
            {
                var rowTolerance = Math.Max(maxHeight, 1.0) * 0.6;
                var sameRow = Math.Abs(previous.Point.Y - sample.Point.Y) <= rowTolerance;
                builder.Append(sameRow ? "" : " ");
            }

            builder.Append(sample.Text);
            previous = sample;
        }

        var text = builder.ToString().Trim();
        return text.Length > 200 ? text.Substring(0, 200) : text;
    }

    private static bool IsSame(ThreePointTextSample a, ThreePointTextSample b)
    {
        return Math.Abs(a.Point.X - b.Point.X) < 1e-6
            && Math.Abs(a.Point.Y - b.Point.Y) < 1e-6
            && string.Equals(a.Text, b.Text, StringComparison.Ordinal);
    }
}
