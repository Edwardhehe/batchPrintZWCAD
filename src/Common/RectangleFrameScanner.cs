using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 矩形框扫描器：扫描一个或多个布局中的 Polyline 矩形，
/// 筛选出符合标准纸张比例的作为待打印图框。
///
/// 公共入口：
///   ScanWindow  — 扫描当前空间（框选范围）
///   ScanScope   — 按范围扫描多个布局（全部/布局/当前/模型）
///
/// 内部流水线：CollectRectanglesFromSpace 收集 →
/// FilterAndPackageRectangles 过滤打包（窗口裁剪 → 纸张比例 →
/// 去重去嵌套 → 空框过滤 → 生成 Result）。
///
/// 支持 WCS 和 UCS（旋转视图），矩形检测使用几何算法而非轴对齐检查。
/// </summary>
public static class RectangleFrameScanner
{
    /// <summary>临时序号标注图层名，扫描时跳过此图层的实体。</summary>
    private const string TemporaryOverlayLayer = "ZBP_TEMP_SEQUENCE_OVERLAY";

    /// <summary>图层可扫描性缓存，避免每次查同一图层都打开图层表。
    /// 静态级缓存，同一次 CAD 会话内跨扫描复用。</summary>
    private static readonly Dictionary<ObjectId, bool> LayerScannableCache = new();

    /// <summary>块定义内容缓存：key=块定义 ObjectId，value=该块定义内的最大矩形（局部坐标）。
    /// 同一次扫描内同一块定义只遍历一次，后续实例直接变换缓存结果。</summary>
    private static readonly Dictionary<ObjectId, List<LocalRectangle>> BlockDefinitionCache = new();

    /// <summary>由 Line 或开放 Polyline 提取的线段，用于识别 4 线段拼合矩形。</summary>
    private struct LineSegment
    {
        public Point3d Start;
        public Point3d End;
    }

    /// <summary>扫描结果：包含一个 PlotJob 和候选纸张列表。</summary>
    public sealed class Result
    {
        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> PaperOptions { get; set; } = new PaperDetection[0];
        /// <summary>矩形 4 个实际角点（WCS 坐标），格式 [x0,y0,x1,y1,x2,y2,x3,y3]。
        /// 用于 DCS 变换（4 点→DCS→取包围盒，和单张打印同理）。null 时用 Job 的包围盒。</summary>
        public double[]? CornerPoints { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // 公共入口
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 扫描指定窗口内的矩形框（仅当前空间），每个匹配的矩形生成一个 PlotJob。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="scanWindow">扫描窗口（WCS 坐标）</param>
    public static List<Result> ScanWindow(Document document, Extents3d scanWindow, double? paperMatchToleranceMm = null)
    {
        LayerScannableCache.Clear();
        BlockDefinitionCache.Clear();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? AppSettingsStore.Load().PaperMatchToleranceMm;
        var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
            ? document.Name
            : document.Database.Filename;

        using var tr = document.Database.TransactionManager.StartTransaction();
        BlockTableRecord owner;
        Layout layout;
        if (document.Database.TileMode)
        {
            var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
            owner = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
        }
        else
        {
            var currentLayoutName = LayoutManager.Current.CurrentLayout;
            var layouts = (DBDictionary)tr.GetObject(document.Database.LayoutDictionaryId, OpenMode.ForRead);
            if (!layouts.Contains(currentLayoutName))
            {
                return new List<Result>();
            }

            layout = (Layout)tr.GetObject(layouts.GetAt(currentLayoutName), OpenMode.ForRead);
            owner = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
        }

        if (!owner.IsLayout || owner.LayoutId.IsNull)
        {
            return new List<Result>();
        }

        var rectangles = CollectRectanglesFromSpace(tr, owner);
        var ownerId = owner.ObjectId;
        var layoutName = layout.LayoutName;
        tr.Commit();

        return FilterAndPackageRectangles(
            document,
            rectangles,
            scanWindow,
            ownerId,
            sourceFile,
            layoutName,
            !layout.ModelType,
            effectivePaperToleranceMm);
    }

    /// <summary>
    /// 按扫描范围扫描多个布局中的矩形框。
    ///
    /// 遍历所有布局，按 <paramref name="scope"/> 决定扫描哪些空间，
    /// 每个空间独立收集矩形、过滤、打包为 Result。结果按布局遍历顺序排列。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="scope">扫描范围</param>
    public static List<Result> ScanScope(Document document, TitleBlockScanScope scope, double? paperMatchToleranceMm = null)
    {
        LayerScannableCache.Clear();
        BlockDefinitionCache.Clear();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? AppSettingsStore.Load().PaperMatchToleranceMm;
        var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
            ? document.Name
            : document.Database.Filename;
        var currentSpaceName = GetCurrentSpaceName(document.Database);

        // 第一阶段：在事务内遍历所有匹配布局，收集矩形
        var spaceData = new List<(List<LocalRectangle> Rectangles, ObjectId OwnerId, string LayoutName, bool IsPaperSpace, int TabOrder)>();
        using (var tr = document.Database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout || owner.LayoutId.IsNull)
                {
                    continue;
                }

                var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                if (!ShouldScanLayout(layout, scope, currentSpaceName))
                {
                    continue;
                }

                var rectangles = CollectRectanglesFromSpace(tr, owner);
                spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
            }

            tr.Commit();
        }

        // 按布局 TabOrder 排序，确保模型空间在最前、图纸布局按选项卡顺序排列
        spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

        // 第二阶段：对每个空间独立过滤打包（FilterEmptyRectangles 内会开自己的事务）
        var allResults = new List<Result>();
        foreach (var (rectangles, ownerId, layoutName, isPaperSpace, _) in spaceData)
        {
            var results = FilterAndPackageRectangles(
                document,
                rectangles,
                null,
                ownerId,
                sourceFile,
                layoutName,
                isPaperSpace,
                effectivePaperToleranceMm);
            allResults.AddRange(results);
        }

        return allResults;
    }

    // ═══════════════════════════════════════════════════════════════
    // 内部：单空间收集 & 过滤打包
    // ═══════════════════════════════════════════════════════════════

    /// <summary>遍历单个空间（BlockTableRecord）内所有顶层实体，递归收集矩形 Polyline。</summary>
    private static List<LocalRectangle> CollectRectanglesFromSpace(Transaction tr, BlockTableRecord owner)
    {
        var rectangles = new List<LocalRectangle>();
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            CollectEntityRectangles(tr, entity, Matrix3d.Identity, rectangles, null, new HashSet<ObjectId>(), 0);
        }

        return rectangles;
    }

    /// <summary>
    /// 对一个空间内收集到的矩形做完整的过滤流水线并打包为 Result 列表。
    ///
    /// 流水线：窗口裁剪（可选）→ 纸张比例过滤 → 去重去嵌套 → 空框过滤 → 生成 Result。
    /// </summary>
    private static List<Result> FilterAndPackageRectangles(
        Document document,
        List<LocalRectangle> rectangles,
        Extents3d? scanWindow,
        ObjectId ownerId,
        string sourceFile,
        string layoutName,
        bool isPaperSpace,
        double paperMatchToleranceMm)
    {
        // 3a. 窗口裁剪（可选）
        List<LocalRectangle> inWindow;
        if (scanWindow.HasValue)
        {
            var window = LocalRectangle.FromPoints(
                scanWindow.Value.MinPoint.X,
                scanWindow.Value.MinPoint.Y,
                scanWindow.Value.MaxPoint.X,
                scanWindow.Value.MaxPoint.Y);
            inWindow = rectangles
                .Where(r => Intersects(r, window))
                .ToList();
        }
        else
        {
            inWindow = rectangles.ToList();
        }

        // 3b. 纸张标准比例过滤
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        var paperMatched = new List<LocalRectangle>();
        var paperOptionsByRect = new Dictionary<LocalRectangle, IReadOnlyList<PaperDetection>>();
        foreach (var rectangle in inWindow)
        {
            var width = rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            // 短边按设置中的毫米容差匹配常用比例；长边先吸附 1/8 模数，超出同一容差才转任意动态纸张。
            var detectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(paperMatchToleranceMm, isPaperSpace);
            var options = PaperSizeDetector.DetectCandidates(width, height, detectionOptions);
            if (options.Count == 0)
            {
                continue;
            }

            paperMatched.Add(rectangle);
            paperOptionsByRect[rectangle] = options;
        }

        // 3c. 去重去嵌套
        var unique = FilterRectangles(paperMatched);

        // 3d. 空框过滤
        var withContent = FilterEmptyRectangles(document, ownerId, unique);

        // 3e. 生成结果
        var results = new List<Result>();
        foreach (var rectangle in withContent)
        {
            var options = paperOptionsByRect[rectangle];
            var paper = options.First();
            var width = rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            var index = results.Count;
            results.Add(new Result
            {
                CornerPoints = rectangle.CornerPoints,
                PaperOptions = options,
                Job = new PlotJob
                {
                    IsManualWindow = true,
                    SourceFile = sourceFile,
                    SpaceName = layoutName,
                    IsPaperSpace = isPaperSpace,
                    DrawingNumber = (index + 1).ToString("D2"),
                    Title = stem,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{width:0.##} x {height:0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = "矩形框批量打印",
                    PaperWidthMm = paper.PaperWidthMm,
                    PaperHeightMm = paper.PaperHeightMm,
                    DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    RequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    MinX = rectangle.MinX,
                    MinY = rectangle.MinY,
                    MaxX = rectangle.MaxX,
                    MaxY = rectangle.MaxY,
                    // 打印窗口后续会转换为 DCS；DWG 拆图仍需保留原始 WCS 四角点。
                    CornerPoints = rectangle.CornerPoints == null
                        ? null
                        : (double[])rectangle.CornerPoints.Clone()
                }
            });
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    // 布局范围判断（与 TitleBlockScanner 一致）
    // ═══════════════════════════════════════════════════════════════

    private static bool ShouldScanLayout(Layout layout, TitleBlockScanScope scope, string? currentSpaceName)
    {
        switch (scope)
        {
            case TitleBlockScanScope.PaperLayouts:
                return !layout.ModelType;
            case TitleBlockScanScope.ModelSpace:
                return layout.ModelType;
            case TitleBlockScanScope.CurrentSpace:
                return IsCurrentSpace(layout, currentSpaceName);
            case TitleBlockScanScope.AllSpaces:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCurrentSpace(Layout layout, string? currentSpaceName)
    {
        if (string.IsNullOrWhiteSpace(currentSpaceName))
        {
            return false;
        }

        return string.Equals(layout.LayoutName, currentSpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentSpaceName(Database db)
    {
        try
        {
            return LayoutManager.Current.CurrentLayout;
        }
        catch
        {
            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var csId = db.CurrentSpaceId;
                if (!csId.IsNull && tr.GetObject(csId, OpenMode.ForRead) is BlockTableRecord btr)
                {
                    var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                    return layout.LayoutName;
                }
            }
            catch
            {
                // 没有打开的文档或数据库不可用
            }

            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 递归实体遍历
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 递归遍历实体及其子实体（块定义内部），检测矩形 Polyline 并收集 Line/Polyline 线段。
    ///
    /// 对每个实体：
    ///   1. 如果是 Polyline → 尝试检测矩形 → 加入结果
    ///   2. 如果是 Line → 提取线段加入 segments
    ///   3. 如果是开放 2 点 Polyline（无圆弧）→ 提取线段加入 segments
    ///   4. 如果是 BlockReference → 进入块定义递归
    ///
    /// 过滤规则：
    ///   - 跳过临时序号标注图层
    ///   - 跳过不可打印图层的实体
    ///   - 跳过 CAD 判定为不可见的实体（动态块隐藏状态等）
    ///   - 跳过被 XCLIP 裁切过的块参照
    ///   - 防循环：同一个块定义只处理一次（visitedDefinitions）
    ///   - 防过深：递归深度上限 12 层
    /// </summary>
    /// <param name="tr">事务</param>
    /// <param name="entity">当前实体</param>
    /// <param name="transform">从当前实体坐标系到 WCS 的累积变换矩阵</param>
    /// <param name="rectangles">收集到的矩形列表</param>
    /// <param name="segments">收集到的线段列表（Line 和开放 Polyline）</param>
    /// <param name="visitedDefinitions">已访问的块定义 ID，防循环</param>
    /// <param name="depth">当前递归深度</param>
    private static void CollectEntityRectangles(
        Transaction tr,
        Entity entity,
        Matrix3d transform,
        ICollection<LocalRectangle> rectangles,
        ICollection<LineSegment> segments,
        ISet<ObjectId> visitedDefinitions,
        int depth)
    {
        // 跳过标注图层——避免把自己的标注当矩形扫进去
        if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ── 分支 0：Line → 收集线段（仅块内部，最多 3 层）──
        if (segments != null && depth <= 3 && entity is Line line && IsEntityLayerScannable(tr, entity))
        {
            segments.Add(new LineSegment
            {
                Start = line.StartPoint.TransformBy(transform),
                End = line.EndPoint.TransformBy(transform)
            });
        }

        // ── 分支 0.5：开放 2 点 Polyline（无圆弧）→ 收集线段（仅块内部，最多 3 层）──
        if (segments != null && depth <= 3 && entity is Polyline plSegment && !plSegment.Closed
            && plSegment.NumberOfVertices == 2
            && IsEntityLayerScannable(tr, entity))
        {
            // 确认无圆弧段
            var hasBulge = false;
            for (var i = 0; i < plSegment.NumberOfVertices; i++)
            {
                if (Math.Abs(plSegment.GetBulgeAt(i)) > 1e-9) { hasBulge = true; break; }
            }

            if (!hasBulge)
            {
                segments.Add(new LineSegment
                {
                    Start = plSegment.GetPoint3dAt(0).TransformBy(transform),
                    End = plSegment.GetPoint3dAt(1).TransformBy(transform)
                });
            }
        }

        // ── 分支 1：Polyline（轻量线）→ 矩形检测 ──
        if (entity is Polyline polyline
            // 图框 PL 可能专门放在 Defpoints 或其他”不打印”辅助层；
            // 它只提供打印边界，不代表该层图素会进入打印内容，因此这里只要求图层开启且未冻结。
            && IsFramePolylineLayerScannable(tr, entity)
            && TryGetRectangle(polyline, transform, out var rectangle))
        {
            // 先全部收集，不去重——不同实例的同一定义各自独立，去重放在后续 FilterRectangles
            rectangles.Add(rectangle);
        }

        // ── 分支 1b：Polyline2d（老式 POLYLINE+VERTEX）→ 矩形检测 ──
        // 旧版 CAD 绘制的图框常用老式多段线，LIST 命令输出 “POLYLINE/VERTEX”，
        // .NET API 类型为 Polyline2d，与轻量 Polyline 不同，需单独处理。
        if (entity is Polyline2d polyline2d
            && IsFramePolylineLayerScannable(tr, entity)
            && TryGetRectangleFrom2d(tr, polyline2d, transform, out var rectangle2d))
        {
            rectangles.Add(rectangle2d);
        }

        // ── 分支 1c：Polyline3d（3DPOLY）→ 矩形检测 ──
        // 三维多段线在 XY 平面上也可构成矩形图框，顶点类型为 Vertex3d。
        if (entity is Polyline3d polyline3d
            && IsFramePolylineLayerScannable(tr, entity)
            && TryGetRectangleFrom3d(tr, polyline3d, transform, out var rectangle3d))
        {
            rectangles.Add(rectangle3d);
        }

        // ── 分支 2：BlockReference → 缓存 + 递归进入 ──
        if (entity is not BlockReference blockReference || depth >= 12)
        {
            return;
        }

        // XCLIP 裁切过的块不参与扫描
        if (IsBlockClipped(tr, blockReference))
        {
            return;
        }

        var definitionId = blockReference.BlockTableRecord;
        var instanceXform = blockReference.BlockTransform * transform;

        // 块定义缓存：同一块定义只遍历一次，后续实例直接变换缓存的局部坐标结果。
        if (BlockDefinitionCache.TryGetValue(definitionId, out var cachedRects))
        {
            if (cachedRects.Count > 0)
            {
                rectangles.Add(TransformCachedRect(cachedRects[0], instanceXform));
            }
            return;
        }

        // 防循环：同一个块定义在一条递归路径上只进入一次
        if (!visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);

            // 用单位矩阵遍历获取块局部坐标，存入缓存供其他实例复用。
            // 同一块定义内只保留最大的矩形框。
            // 四线段拼合在块内独立处理；深度 ≤ 3 层。
            // 合并预扫和主循环为单次遍历：边数线段边收集矩形。
            var localRects = new List<LocalRectangle>();
            var lineCount = 0;
            var limitSegments = depth >= 3;
            var blockSegments = limitSegments ? null : new List<LineSegment>();
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity nested)
                {
                    continue;
                }

                // 递归阶段不能按“是否打印”提前截断，否则块内不可打印层上的图框 PL
                // 永远到不了下面的矩形检测分支。普通 Line 是否可拼框仍由严格规则单独控制。
                if (!IsEntityLayerVisibleForScanning(tr, nested))
                {
                    continue;
                }

                if (!IsEntityVisible(nested))
                {
                    continue;
                }

                // 线段计数（与收集合并）：超过 200 条则停止收集线段
                if (!limitSegments && (nested is Line || (nested is Polyline pl && !pl.Closed && pl.NumberOfVertices == 2)))
                {
                    if (++lineCount > 200)
                    {
                        limitSegments = true;
                        blockSegments = null;
                    }
                }

                // 用单位矩阵递归 → 子实体结果均在当前块定义局部坐标下
                CollectEntityRectangles(tr, nested, Matrix3d.Identity, localRects, blockSegments, visitedDefinitions, depth + 1);
            }

            if (blockSegments != null && blockSegments.Count >= 4)
            {
                var segRects = FindRectanglesFromSegments(blockSegments);
                localRects.AddRange(segRects);
            }

            // 缓存局部坐标下的最大矩形，并变换到当前实例的世界坐标
            List<LocalRectangle> cacheEntry;
            if (localRects.Count > 0)
            {
                var largest = localRects.OrderByDescending(Area).First();
                cacheEntry = new List<LocalRectangle> { largest };
                rectangles.Add(TransformCachedRect(largest, instanceXform));
            }
            else
            {
                cacheEntry = new List<LocalRectangle>();
            }

            BlockDefinitionCache[definitionId] = cacheEntry;
        }
        catch
        {
            // 个别块定义可能损坏或无权限访问，跳过不影响整体扫描
        }
        finally
        {
            // 离开时移除，允许其他路径再次进入同一定义（不同父级下可重复）
            visitedDefinitions.Remove(definitionId);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 四线段拼合矩形检测
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从独立线段集合中找出由 4 条线段首尾连接而成的矩形。
    ///
    /// 算法：
    ///   1. 端点聚类：将坐标按容差网格分组，构建端点→线段索引的哈希表
    ///   2. 图遍历：从每条线段出发，沿连接关系走 4 步，找回到起点的闭环
    ///   3. 几何验证：对角线等长 + 中点重合，复用 TryGetRectangle 的判定逻辑
    ///   4. 防重复：对 4 条线段索引排序生成去重 key
    /// </summary>
    /// <param name="segments">从 Line 和开放 Polyline 提取的线段列表</param>
    /// <returns>识别出的矩形 LocalRectangle 列表</returns>
    private static List<LocalRectangle> FindRectanglesFromSegments(List<LineSegment> segments)
    {
        var rectangles = new List<LocalRectangle>();
        if (segments.Count < 4)
        {
            return rectangles;
        }

        // 端点容差（CAD 绘图单位，通常即 mm）
        const double endpointTolerance = 0.5;

        // ── 第 1 步：端点聚类，构建哈希表 ──
        // key: (xGrid, yGrid) 容差网格坐标；value: 以此点为端点的线段索引列表
        var endpointMap = new Dictionary<(long X, long Y), List<int>>();
        for (var i = 0; i < segments.Count; i++)
        {
            AddEndpoint(endpointMap, segments[i].Start, i, endpointTolerance);
            AddEndpoint(endpointMap, segments[i].End, i, endpointTolerance);
        }

        // ── 第 2 步：查找与指定点连接的线段 ──
        // 查 3×3 邻域网格，避免端点恰好在网格边界两侧时漏掉
        // 端点匹配用欧几里得距离，比曼哈顿距离更严格
        bool IsNear(Point3d a, Point3d b, double tol)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy <= tol * tol;
        }

        List<int> FindConnected(int excludeIndex, Point3d point)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            var baseKey = GridKey(point, endpointTolerance);
            for (var dx = -1L; dx <= 1; dx++)
            {
                for (var dy = -1L; dy <= 1; dy++)
                {
                    (long X, long Y) key = (baseKey.Item1 + dx, baseKey.Item2 + dy);
                    if (!endpointMap.TryGetValue(key, out var list))
                    {
                        continue;
                    }

                    foreach (var i in list)
                    {
                        if (i == excludeIndex || !seen.Add(i))
                        {
                            continue;
                        }

                        var s = segments[i];
                        if (IsNear(s.Start, point, endpointTolerance)
                            || IsNear(s.End, point, endpointTolerance))
                        {
                            result.Add(i);
                            // 矩形角点正常只连 1~2 条线；超过上限说明是密集汇聚点，剪枝防组合爆炸
                            if (result.Count >= 8)
                            {
                                return result;
                            }
                        }
                    }
                }
            }

            return result;
        }

        // 获取线段的"另一端"（与 point 连接的那端的对面端点）
        // 两端都不匹配时返回极远点，让几何验证自动拦截
        Point3d OtherEnd(int segIndex, Point3d point)
        {
            var s = segments[segIndex];
            if (IsNear(s.Start, point, endpointTolerance))
            {
                return s.End;
            }

            if (IsNear(s.End, point, endpointTolerance))
            {
                return s.Start;
            }

            return new Point3d(double.MaxValue, double.MaxValue, 0);
        }

        // ── 第 3 步：遍历找矩形 ──
        var foundKeys = new HashSet<string>();

        for (var i1 = 0; i1 < segments.Count; i1++)
        {
            var s1 = segments[i1];
            var A = s1.Start;
            var B = s1.End;

            var connectedB = FindConnected(i1, B);
            // 端点 B 汇聚太多线段（≥8），不可能是矩形角点，整条跳
            if (connectedB.Count >= 8)
            {
                continue;
            }

            foreach (var i2 in connectedB)
            {
                var C = OtherEnd(i2, B);

                foreach (var i3 in FindConnected(i1, C).Where(j => j != i2))
                {
                    var D = OtherEnd(i3, C);

                    foreach (var i4 in FindConnected(i1, D).Where(j => j != i2 && j != i3))
                    {
                        var backToA = OtherEnd(i4, D);
                        if (!IsNear(backToA, A, endpointTolerance))
                        {
                            continue;
                        }

                        // 防重复：4 条线段索引排序生成唯一 key
                        var ids = new[] { i1, i2, i3, i4 };
                        Array.Sort(ids);
                        var key = string.Join(",", ids);
                        if (!foundKeys.Add(key))
                        {
                            continue;
                        }

                        // ── 第 4 步：矩形几何验证 ──
                        var corners = new[] { A, B, C, D };
                        if (TryBuildRectangleFromCorners(corners, out var rectangle))
                        {
                            rectangles.Add(rectangle);
                        }
                    }
                }
            }
        }

        return rectangles;
    }

    /// <summary>将端点按容差网格分组，添加到映射表。</summary>
    private static void AddEndpoint(Dictionary<(long, long), List<int>> map, Point3d point, int segmentIndex, double tolerance)
    {
        var key = GridKey(point, tolerance);
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<int>();
            map[key] = list;
        }

        list.Add(segmentIndex);
    }

    /// <summary>生成容差网格坐标 key：(Round(x/tol), Round(y/tol))。</summary>
    private static (long, long) GridKey(Point3d point, double tolerance)
    {
        return (
            (long)Math.Round(point.X / tolerance),
            (long)Math.Round(point.Y / tolerance));
    }

    /// <summary>
    /// 将 4 个角点验证为矩形，验证逻辑与 TryGetRectangle 一致：
    /// 对角线等长 + 中点重合，生成 LocalRectangle。
    /// </summary>
    private static bool TryBuildRectangleFromCorners(Point3d[] corners, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();

        // 包围盒
        var minX = corners.Min(p => p.X);
        var minY = corners.Min(p => p.Y);
        var maxX = corners.Max(p => p.X);
        var maxY = corners.Max(p => p.Y);
        var boxWidth = maxX - minX;
        var boxHeight = maxY - minY;
        if (boxWidth <= 1e-6 || boxHeight <= 1e-6)
        {
            return false;
        }

        var tolerance = Math.Max(boxWidth, boxHeight) * 0.001;

        // 4 个角点必须互不相同
        for (var i = 0; i < 4; i++)
        {
            for (var j = i + 1; j < 4; j++)
            {
                if (SamePoint(corners[i], corners[j], tolerance))
                {
                    return false;
                }
            }
        }

        // corners[0-3] = A, B, C, D（按遍历顺序绕周长排列）
        // 对角线：A-C 和 B-D
        var d02 = corners[0].DistanceTo(corners[2]);
        var d13 = corners[1].DistanceTo(corners[3]);
        if (Math.Abs(d02 - d13) > tolerance)
        {
            return false;
        }

        var mid02 = new Point3d(
            (corners[0].X + corners[2].X) / 2d,
            (corners[0].Y + corners[2].Y) / 2d, 0);
        var mid13 = new Point3d(
            (corners[1].X + corners[3].X) / 2d,
            (corners[1].Y + corners[3].Y) / 2d, 0);
        if (mid02.DistanceTo(mid13) > tolerance)
        {
            return false;
        }

        // 计算实际边长（用于纸张检测，UCS 旋转下包围盒与实际边长不同）
        var side01 = corners[0].DistanceTo(corners[1]);
        var side12 = corners[1].DistanceTo(corners[2]);
        var actualWidth = Math.Max(side01, side12);
        var actualHeight = Math.Min(side01, side12);

        rectangle = LocalRectangle.FromPoints(minX, minY, maxX, maxY);
        rectangle.ActualWidth = actualWidth;
        rectangle.ActualHeight = actualHeight;
        rectangle.CornerPoints = new[]
        {
            corners[0].X, corners[0].Y,
            corners[1].X, corners[1].Y,
            corners[2].X, corners[2].Y,
            corners[3].X, corners[3].Y
        };
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // 可见性 & 图层过滤
    // ═══════════════════════════════════════════════════════════════

    /// <summary>查询实体在 CAD 引擎中的可见性。动态块隐藏状态自动为 false。</summary>
    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch
        {
            // 老版本 API 可能无此属性，宁可多扫不丢
            return true;
        }
    }

    /// <summary>
    /// 图层是否可扫描：必须在开启、未冻结、可打印的图层上。
    /// 关闭或冻结的图层通常对应动态块的隐藏状态。
    /// 结果缓存在 LayerScannableCache 中，同一图层只需查一次。
    /// </summary>
    private static bool IsEntityLayerScannable(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull)
            {
                return false;
            }

            if (LayerScannableCache.TryGetValue(entity.LayerId, out var cached))
            {
                return cached;
            }

            if (tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                LayerScannableCache[entity.LayerId] = false;
                return false;
            }

            var result = !layer.IsOff      // 图层未关闭
                && !layer.IsFrozen         // 图层未冻结
                && layer.IsPlottable;      // 图层可打印
            LayerScannableCache[entity.LayerId] = result;
            return result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 图框 PL 的图层过滤规则：图层必须开启且未冻结，但允许设置为“不打印”。
    /// 不打印属性只决定图素是否输出，不应阻止该 PL 作为矩形框批打的边界。
    /// </summary>
    private static bool IsFramePolylineLayerScannable(Transaction tr, Entity entity)
    {
        return IsEntityLayerVisibleForScanning(tr, entity);
    }

    /// <summary>
    /// 判断图层在当前图形中是否可见，仅检查关闭/冻结状态，不检查 IsPlottable。
    /// 此规则只用于查找图框 PL 和递归进入块定义；内容过滤仍调用严格的
    /// <see cref="IsEntityLayerScannable"/>，不会把不打印层误当成有效图纸内容。
    /// </summary>
    private static bool IsEntityLayerVisibleForScanning(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull
                || tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                return false;
            }

            return !layer.IsOff && !layer.IsFrozen;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查块参照是否被 XCLIP 命令裁切过。
    /// XCLIP 通过扩展字典中的 "ACAD_FILTER" 条目存储裁切边界，
    /// 裁切后的块参照显示不全，内部矩形框不应参与扫描。
    /// </summary>
    private static bool IsBlockClipped(Transaction tr, BlockReference blockRef)
    {
        try
        {
            if (blockRef.ExtensionDictionary == ObjectId.Null)
            {
                return false;
            }

            var extDict = (DBDictionary)tr.GetObject(blockRef.ExtensionDictionary, OpenMode.ForRead);
            return extDict.Contains("ACAD_FILTER");
        }
        catch
        {
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 矩形检测（核心算法）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 检测 Polyline 是否为矩形。支持任意方向的矩形（UCS 旋转下也可检测）。
    ///
    /// 算法流程：
    ///   1. 基本检查：顶点数≥4、无圆弧段（bulge=0）
    ///   2. 闭合处理：未闭合的首尾点相近也算闭合
    ///   3. 去连续重复点
    ///   4. 去共线中间点（叉积法，旋转无关）
    ///   5. 精简后必须剩 4 点
    ///   6. 几何矩形验证：对角线等长 + 中点重合（替代旧的轴对齐检查）
    ///   7. 计算实际边长用于纸张检测
    ///   8. 保存包围盒（位置/相交用）和实际角点（DCS 变换用）
    /// </summary>
    /// <param name="polyline">待检测的多段线</param>
    /// <param name="transform">累积变换矩阵（块参照嵌套时的坐标变换）</param>
    /// <param name="rectangle">输出的矩形数据（包围盒 + 实际边长 + 角点）</param>
    /// <returns>true 表示是有效矩形</returns>
    /// <summary>
    /// 从 Polyline3d（3DPOLY）提取矩形，逻辑与 TryGetRectangleFrom2d 一致。
    /// Polyline3d 只有直线段，无圆弧；顶点类型为 Vertex3d，只取线型顶点（枚举值=0）。
    /// </summary>
    private static bool TryGetRectangleFrom3d(Transaction tr, Polyline3d polyline3d, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        var points = new List<Point3d>();
        foreach (ObjectId vertexId in polyline3d)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead, false) is not PolylineVertex3d vertex)
                continue;
            // 只接受线型顶点（枚举值=0），跳过样条曲线控制点
            if ((int)vertex.VertexType != 0)
                continue;
            // 压平 Z 坐标，允许图框略微不在 XY 平面上
            var wcs = vertex.Position.TransformBy(transform);
            points.Add(new Point3d(wcs.X, wcs.Y, 0));
        }

        if (points.Count < 4) return false;

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        var boxWidth = maxX - minX;
        var boxHeight = maxY - minY;
        if (boxWidth <= 1e-6 || boxHeight <= 1e-6) return false;

        var tolerance = Math.Max(boxWidth, boxHeight) * 0.001;

        if (points.Count > 1 && SamePoint(points[0], points[points.Count - 1], tolerance))
            points.RemoveAt(points.Count - 1);

        RemoveConsecutiveDuplicatePoints(points, tolerance);
        RemoveCollinearPoints(points, tolerance);

        if (points.Count != 4) return false;

        var d02 = points[0].DistanceTo(points[2]);
        var d13 = points[1].DistanceTo(points[3]);
        if (Math.Abs(d02 - d13) > tolerance) return false;

        var mid02 = new Point3d((points[0].X + points[2].X) / 2d, (points[0].Y + points[2].Y) / 2d, 0);
        var mid13 = new Point3d((points[1].X + points[3].X) / 2d, (points[1].Y + points[3].Y) / 2d, 0);
        if (mid02.DistanceTo(mid13) > tolerance) return false;

        var side0 = points[0].DistanceTo(points[1]);
        var side1 = points[1].DistanceTo(points[2]);
        var actualWidth = Math.Max(side0, side1);
        var actualHeight = Math.Min(side0, side1);

        rectangle = new LocalRectangle
        {
            MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            ActualWidth = actualWidth, ActualHeight = actualHeight,
            CornerPoints = new[]
            {
                points[0].X, points[0].Y,
                points[1].X, points[1].Y,
                points[2].X, points[2].Y,
                points[3].X, points[3].Y
            }
        };
        return true;
    }

    /// <summary>
    /// 从老式 Polyline2d（POLYLINE+VERTEX）提取矩形，逻辑与 TryGetRectangle 一致。
    /// Polyline2d 的顶点存储在子实体中，需通过事务逐个读取。
    /// </summary>
    private static bool TryGetRectangleFrom2d(Transaction tr, Polyline2d polyline2d, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        var points = new List<Point3d>();
        foreach (ObjectId vertexId in polyline2d)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead, false) is not Vertex2d vertex)
                continue;
            // 只接受普通顶点（枚举值=0），跳过样条曲线控制点等非几何顶点
            if ((int)vertex.VertexType != 0)
                continue;
            // 圆弧段判断
            if (Math.Abs(vertex.Bulge) > 1e-9) return false;
            points.Add(vertex.Position.TransformBy(transform));
        }

        if (points.Count < 4) return false;

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        var boxWidth = maxX - minX;
        var boxHeight = maxY - minY;
        if (boxWidth <= 1e-6 || boxHeight <= 1e-6) return false;

        var tolerance = Math.Max(boxWidth, boxHeight) * 0.001;

        // 去掉与首点重合的尾点（闭合时最后一个顶点等于第一个）
        if (points.Count > 1 && SamePoint(points[0], points[points.Count - 1], tolerance))
            points.RemoveAt(points.Count - 1);

        RemoveConsecutiveDuplicatePoints(points, tolerance);
        RemoveCollinearPoints(points, tolerance);

        if (points.Count != 4) return false;

        // 矩形验证：对角线等长 + 中点重合
        var d02 = points[0].DistanceTo(points[2]);
        var d13 = points[1].DistanceTo(points[3]);
        if (Math.Abs(d02 - d13) > tolerance) return false;

        var mid02 = new Point3d((points[0].X + points[2].X) / 2d, (points[0].Y + points[2].Y) / 2d, 0);
        var mid13 = new Point3d((points[1].X + points[3].X) / 2d, (points[1].Y + points[3].Y) / 2d, 0);
        if (mid02.DistanceTo(mid13) > tolerance) return false;

        // 计算实际边长（相邻两点距离）
        var side0 = points[0].DistanceTo(points[1]);
        var side1 = points[1].DistanceTo(points[2]);
        var actualWidth = Math.Max(side0, side1);
        var actualHeight = Math.Min(side0, side1);

        rectangle = new LocalRectangle
        {
            MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
            ActualWidth = actualWidth, ActualHeight = actualHeight,
            CornerPoints = new[]
            {
                points[0].X, points[0].Y,
                points[1].X, points[1].Y,
                points[2].X, points[2].Y,
                points[3].X, points[3].Y
            }
        };
        return true;
    }

    private static bool TryGetRectangle(Polyline polyline, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();

        // ── 第 1 步：基本检查 ──
        if (polyline.NumberOfVertices < 4)
        {
            return false;
        }

        // bulge ≠ 0 表示圆弧段，不是矩形
        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            if (Math.Abs(polyline.GetBulgeAt(index)) > 1e-9)
            {
                return false;
            }
        }

        // ── 第 2 步：提取所有顶点并变换到 WCS ──
        var points = Enumerable.Range(0, polyline.NumberOfVertices)
            .Select(index =>
            {
                var point = polyline.GetPoint3dAt(index);  // 块内坐标
                return point.TransformBy(transform);        // → WCS 坐标
            })
            .ToList();

        // 取包围盒用于后续容差计算
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        var boxWidth = maxX - minX;
        var boxHeight = maxY - minY;
        if (boxWidth <= 1e-6 || boxHeight <= 1e-6)
        {
            return false;
        }

        // 容差：包围盒长边的 0.1%
        var tolerance = Math.Max(boxWidth, boxHeight) * 0.001;

        // ── 第 3 步：闭合处理 ──
        // 未闭合但首尾很近 → 视为闭合；已闭合的去掉重复的尾点
        if (!polyline.Closed && !SamePoint(points[0], points[points.Count - 1], tolerance))
        {
            return false;
        }

        if (points.Count > 1 && SamePoint(points[0], points[points.Count - 1], tolerance))
        {
            points.RemoveAt(points.Count - 1);
        }

        // ── 第 4 步：精简顶点 ──
        RemoveConsecutiveDuplicatePoints(points, tolerance); // 去掉连续重复点
        RemoveCollinearPoints(points, tolerance);             // 去掉边上的共线中间点

        // ── 第 5 步：精简后必须是 4 个顶点 ──
        if (points.Count != 4)
        {
            return false;
        }

        // ── 第 6 步：几何矩形验证 ──
        // 矩形的充要条件（任意方向）：对角线等长 且 对角线中点重合
        // 此方法不依赖轴对齐，UCS 旋转下同样有效
        var d02 = points[0].DistanceTo(points[2]);  // 对角线 0→2
        var d13 = points[1].DistanceTo(points[3]);  // 对角线 1→3
        if (Math.Abs(d02 - d13) > tolerance)
        {
            return false;
        }

        // 两条对角线的中点应该重合（平行四边形）
        var mid02 = new Point3d(
            (points[0].X + points[2].X) / 2d,
            (points[0].Y + points[2].Y) / 2d, 0);
        var mid13 = new Point3d(
            (points[1].X + points[3].X) / 2d,
            (points[1].Y + points[3].Y) / 2d, 0);
        if (mid02.DistanceTo(mid13) > tolerance)
        {
            return false;
        }

        // ── 第 7 步：计算实际边长（用于纸张检测）──
        // 不能用包围盒宽高——UCS 旋转时包围盒比实际矩形大
        // 例如 45° 旋转的 A2 矩形，包围盒可能被误判为 A1
        var side01 = points[0].DistanceTo(points[1]);  // 边 0→1
        var side12 = points[1].DistanceTo(points[2]);  // 边 1→2
        var actualWidth = Math.Max(side01, side12);     // 长边 = 宽度
        var actualHeight = Math.Min(side01, side12);    // 短边 = 高度

        // ── 第 8 步：输出 ──
        // 包围盒用于位置判断和相交检测（Intersects、Contains 等）
        // 实际边长用于纸张识别
        // 实际角点用于 DCS 变换（4 点 × WCS→DCS → 取包围盒，和单张打印算法一致）
        rectangle = LocalRectangle.FromPoints(minX, minY, maxX, maxY);
        rectangle.ActualWidth = actualWidth;
        rectangle.ActualHeight = actualHeight;
        rectangle.CornerPoints = new[]
        {
            points[0].X, points[0].Y,
            points[1].X, points[1].Y,
            points[2].X, points[2].Y,
            points[3].X, points[3].Y
        };
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // 顶点精简
    // ═══════════════════════════════════════════════════════════════

    /// <summary>移除连续重复的顶点（相邻两点距离小于容差时去重）。</summary>
    private static void RemoveConsecutiveDuplicatePoints(IList<Point3d> points, double tolerance)
    {
        for (var index = points.Count - 1; index > 0; index--)
        {
            if (SamePoint(points[index], points[index - 1], tolerance))
            {
                points.RemoveAt(index);
            }
        }
    }

    /// <summary>
    /// 移除边上的共线中间点（三点共线时删除中间点）。
    ///
    /// 使用向量叉积判断共线性：
    ///   v1 = current - previous, v2 = next - current
    ///   crossZ = v1.x × v2.y - v1.y × v2.x
    ///   |crossZ| ≈ 0 → 三点共线 → 删除 current
    ///
    /// 叉积法不依赖坐标轴方向，UCS 旋转下同样有效。
    /// </summary>
    private static void RemoveCollinearPoints(IList<Point3d> points, double tolerance)
    {
        var changed = true;
        while (changed && points.Count > 4)
        {
            changed = false;
            for (var index = 0; index < points.Count; index++)
            {
                var previous = points[(index - 1 + points.Count) % points.Count];
                var current = points[index];
                var next = points[(index + 1) % points.Count];

                // 向量叉积 (v1 × v2)_z
                // = 0 表示 v1 // v2，即三点共线
                var v1x = current.X - previous.X;
                var v1y = current.Y - previous.Y;
                var v2x = next.X - current.X;
                var v2y = next.Y - current.Y;
                var crossZ = v1x * v2y - v1y * v2x;

                if (Math.Abs(crossZ) > tolerance)
                {
                    continue;  // 不共线，保留
                }

                // 共线 → 删除中间点，重新开始检测
                points.RemoveAt(index);
                changed = true;
                break;
            }
        }
    }

    /// <summary>两点是否重合（XY 分量差均在容差内）。</summary>
    private static bool SamePoint(Point3d a, Point3d b, double tolerance)
    {
        return Math.Abs(a.X - b.X) <= tolerance
            && Math.Abs(a.Y - b.Y) <= tolerance;
    }

    // ═══════════════════════════════════════════════════════════════
    // 矩形过滤：去重、去嵌套
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 过滤矩形列表：去重 + 去嵌套。
    ///
    /// 算法：
    ///   1. 按面积降序排列（先处理大的）
    ///   2. 去重：两个矩形边界相同 或 高度重叠（>90%），只保留第一个
    ///   3. 去嵌套：小矩形完全被大矩形包含且面积差 >1.5 倍，移除小的
    /// </summary>
    private static List<LocalRectangle> FilterRectangles(IEnumerable<LocalRectangle> source)
    {
        var unique = new List<LocalRectangle>();

        // 按面积降序遍历，确保大的先进入 unique 列表
        foreach (var rectangle in source.OrderByDescending(Area))
        {
            var tolerance = Math.Max(rectangle.MaxX - rectangle.MinX, rectangle.MaxY - rectangle.MinY) * 0.002;
            // 和已保留的矩形比较：边界相同或高度重叠 → 视为重复，跳过
            if (unique.Any(existing =>
                    SameBounds(existing, rectangle, tolerance)
                    || HasDuplicateOverlap(existing, rectangle)))
            {
                continue;
            }

            unique.Add(rectangle);
        }

        // 去嵌套：如果一个矩形容纳另一个且面积明显更大，移除小的
        return unique
            .Where(candidate => !unique.Any(container =>
                !ReferenceEquals(container, candidate)
                && Area(container) >= Area(candidate) * 1.5  // 容积面积 > 候选 × 1.5
                && Contains(container, candidate)))           // 完全包含
            .ToList();
    }

    /// <summary>两个矩形包围盒是否相同（四个边界都在容差内）。</summary>
    private static bool SameBounds(LocalRectangle a, LocalRectangle b, double tolerance)
    {
        return Math.Abs(a.MinX - b.MinX) <= tolerance
            && Math.Abs(a.MinY - b.MinY) <= tolerance
            && Math.Abs(a.MaxX - b.MaxX) <= tolerance
            && Math.Abs(a.MaxY - b.MaxY) <= tolerance;
    }

    /// <summary>
    /// 两个矩形是否高度重叠（视为同一图纸的多重描边）。
    ///
    /// 判断标准：
    ///   - 重叠面积 ≥ 较小矩形面积的 90%（绝大部分重合）
    ///   - 重叠面积 ≥ 较大矩形面积的 82%（不是小矩形套在大矩形角落）
    ///   - 宽高相似度均 ≥ 90%（尺寸几乎一样）
    /// </summary>
    private static bool HasDuplicateOverlap(LocalRectangle a, LocalRectangle b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return false;
        }

        var areaA = Area(a);
        var areaB = Area(b);
        if (areaA <= 0 || areaB <= 0)
        {
            return false;
        }

        // 重叠占小矩形的比例
        var smallerCoverage = overlapArea / Math.Min(areaA, areaB);
        // 重叠占大矩形的比例
        var largerCoverage = overlapArea / Math.Max(areaA, areaB);
        // 宽高相似度：排除同心嵌套（如 A2 外框 + A3 内框）
        var widthSimilarity = Math.Min(a.MaxX - a.MinX, b.MaxX - b.MinX)
            / Math.Max(a.MaxX - a.MinX, b.MaxX - b.MinX);
        var heightSimilarity = Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY)
            / Math.Max(a.MaxY - a.MinY, b.MaxY - b.MinY);

        return smallerCoverage >= 0.90
            && largerCoverage >= 0.82
            && widthSimilarity >= 0.90
            && heightSimilarity >= 0.90;
    }

    // ═══════════════════════════════════════════════════════════════
    // 几何工具
    // ═══════════════════════════════════════════════════════════════

    /// <summary>outer 是否完全包含 inner（含 0.3% 容差）。</summary>
    private static bool Contains(LocalRectangle outer, LocalRectangle inner)
    {
        var tolerance = Math.Max(outer.MaxX - outer.MinX, outer.MaxY - outer.MinY) * 0.003;
        return inner.MinX >= outer.MinX - tolerance
            && inner.MinY >= outer.MinY - tolerance
            && inner.MaxX <= outer.MaxX + tolerance
            && inner.MaxY <= outer.MaxY + tolerance;
    }

    /// <summary>矩形包围盒面积（>=0）。</summary>
    private static double Area(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    /// <summary>
    /// 将缓存的局部坐标矩形通过变换矩阵转到世界坐标。
    /// 根据四角点重新计算包围盒，保持 ActualWidth/ActualHeight 不变。
    /// </summary>
    private static LocalRectangle TransformCachedRect(LocalRectangle localRect, Matrix3d xform)
    {
        if (localRect.CornerPoints == null)
        {
            // 无角点的退化情况：直接变换 Min/Max 角点取包围盒
            var pMin = new Point3d(localRect.MinX, localRect.MinY, 0).TransformBy(xform);
            var pMax = new Point3d(localRect.MaxX, localRect.MaxY, 0).TransformBy(xform);
            return LocalRectangle.FromPoints(
                Math.Min(pMin.X, pMax.X), Math.Min(pMin.Y, pMax.Y),
                Math.Max(pMin.X, pMax.X), Math.Max(pMin.Y, pMax.Y));
        }

        var cp = localRect.CornerPoints;
        var p0 = new Point3d(cp[0], cp[1], 0).TransformBy(xform);
        var p1 = new Point3d(cp[2], cp[3], 0).TransformBy(xform);
        var p2 = new Point3d(cp[4], cp[5], 0).TransformBy(xform);
        var p3 = new Point3d(cp[6], cp[7], 0).TransformBy(xform);

        var minX = Math.Min(Math.Min(p0.X, p1.X), Math.Min(p2.X, p3.X));
        var minY = Math.Min(Math.Min(p0.Y, p1.Y), Math.Min(p2.Y, p3.Y));
        var maxX = Math.Max(Math.Max(p0.X, p1.X), Math.Max(p2.X, p3.X));
        var maxY = Math.Max(Math.Max(p0.Y, p1.Y), Math.Max(p2.Y, p3.Y));

        var result = LocalRectangle.FromPoints(minX, minY, maxX, maxY);
        result.ActualWidth = localRect.ActualWidth;
        result.ActualHeight = localRect.ActualHeight;
        result.CornerPoints = new[] { p0.X, p0.Y, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y };
        return result;
    }

    /// <summary>两个轴对齐矩形是否相交。</summary>
    private static bool Intersects(LocalRectangle rectangle, LocalRectangle window)
    {
        return rectangle.MaxX >= window.MinX
            && rectangle.MinX <= window.MaxX
            && rectangle.MaxY >= window.MinY
            && rectangle.MinY <= window.MaxY;
    }

    // ═══════════════════════════════════════════════════════════════
    // 空框过滤：检查矩形内是否存在实际的绘图内容（图素）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 过滤掉没有任何可见可打印图素的空矩形框。
    ///
    /// 对每个候选矩形，递归遍历布局内的所有实体，检查是否有至少一个
    /// 可见、可打印、非矩形框自身的实体落入矩形范围内。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="ownerId">布局 BlockTableRecord 的 ObjectId</param>
    /// <param name="candidates">待检查的候选矩形列表</param>
    /// <returns>包含实际图素的矩形列表</returns>
	    /// <summary>
	    /// 空框过滤：先预扫描一次收集所有实体的世界坐标外包盒（含嵌套块），
	    /// 再对每个候选矩形做内存级包围盒相交判断。
	    /// 避免对每个矩形都遍历 CAD 数据库——O(R×E) → O(E + R×B)。
	    /// </summary>
	    private static List<LocalRectangle> FilterEmptyRectangles(
	        Document document, ObjectId ownerId, List<LocalRectangle> candidates)
	    {
	        using var tr = document.Database.TransactionManager.StartTransaction();
	        var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);

	        // 预扫描：一次性收集所有实体的世界坐标外包盒（含嵌套块）
	        var entityBoxes = new List<LocalRectangle>();
	        CollectEntityBoxesForContentCheck(tr, owner, Matrix3d.Identity, entityBoxes,
	            new HashSet<ObjectId>(), 0);

	        // 内存级矩形相交判断，不再访问 CAD 数据库
	        var result = new List<LocalRectangle>();
	        foreach (var rect in candidates)
	        {
	            if (AnyIntersects(entityBoxes, rect))
	            {
	                result.Add(rect);
	            }
	        }

	        return result;
	    }

	    /// <summary>
	    /// 递归收集空间内所有实体的世界坐标外包盒，过滤规则与旧 CheckEntityContent 一致：
	    /// 跳过临时标注图层、不可打印图层、不可见实体；块参照递归进入（防循环、防过深）。
	    /// visitedDefinitions 的 Add/Remove 模式允许同一块定义从不同父路径重新进入。
	    /// </summary>
	    private static void CollectEntityBoxesForContentCheck(
	        Transaction tr,
	        BlockTableRecord owner,
	        Matrix3d transform,
	        List<LocalRectangle> boxes,
	        HashSet<ObjectId> visitedDefinitions,
	        int depth)
	    {
	        foreach (ObjectId id in owner)
	        {
	            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
	            {
	                continue;
	            }

	            // 跳过临时标注图层
	            if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
	            {
	                continue;
	            }

	            // 跳过不可打印图层的实体
	            if (!IsEntityLayerScannable(tr, entity))
	            {
	                continue;
	            }

	            // 跳过不可见实体
	            if (!IsEntityVisible(entity))
	            {
	                continue;
	            }

	            // 收集当前实体的世界坐标外包盒
	            try
	            {
	                var ext = entity.GeometricExtents;
	                var extMin = ext.MinPoint.TransformBy(transform);
	                var extMax = ext.MaxPoint.TransformBy(transform);
	                boxes.Add(LocalRectangle.FromPoints(
	                    Math.Min(extMin.X, extMax.X), Math.Min(extMin.Y, extMax.Y),
	                    Math.Max(extMin.X, extMax.X), Math.Max(extMin.Y, extMax.Y)));
	            }
	            catch
	            {
	                // 部分实体（如空块参照）可能抛出异常，忽略
	            }

	            // ── 递归进入块参照 ──
	            if (entity is not BlockReference blockRef || depth >= 5)
	            {
	                continue;
	            }

	            if (IsBlockClipped(tr, blockRef))
	            {
	                continue;
	            }

	            var definitionId = blockRef.BlockTableRecord;
	            if (!visitedDefinitions.Add(definitionId))
	            {
	                continue;
	            }

	            try
	            {
	                var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
	                var nestedTransform = blockRef.BlockTransform * transform;
	                CollectEntityBoxesForContentCheck(tr, definition, nestedTransform, boxes,
	                    visitedDefinitions, depth + 1);
	            }
	            catch
	            {
	                // 损坏的块定义无法读取，跳过
	            }

	            visitedDefinitions.Remove(definitionId);
	        }
	    }

	    /// <summary>
	    /// 检查预收集的实体外包盒列表中是否有任意一个与目标矩形相交。
	    /// 纯内存操作，短路退出。
	    /// </summary>
	    private static bool AnyIntersects(List<LocalRectangle> boxes, LocalRectangle target)
	    {
	        foreach (var box in boxes)
	        {
	            if (Intersects(box, target))
	            {
	                return true;
	            }
	        }

	        return false;
	    }
	}
