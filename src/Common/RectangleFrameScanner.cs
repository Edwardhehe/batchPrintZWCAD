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
/// 矩形框扫描器：扫描一个或多个布局中的闭合 Polyline 矩形；
/// 用户开启对应设置后，也识别由 4 个独立直线或直线型开放 PL 首尾相连组成的矩形，
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
        public string EntityHandle;
    }

    /// <summary>四叉树中的端点记录；同一线段的两个端点分别插入。</summary>
    private struct SegmentEndpoint
    {
        public Point3d Point;
        public int SegmentIndex;
    }

    /// <summary>
    /// 二维端点四叉树。只负责按小范围查找相邻端点，矩形闭环和几何正确性仍由后续算法验证。
    /// </summary>
    private sealed class EndpointQuadtree
    {
        private const int NodeCapacity = 16;
        private const int MaximumDepth = 16;

        private readonly double _minX;
        private readonly double _minY;
        private readonly double _maxX;
        private readonly double _maxY;
        private readonly int _depth;
        private readonly List<SegmentEndpoint> _items = new();
        private EndpointQuadtree[]? _children;

        public EndpointQuadtree(double minX, double minY, double maxX, double maxY, int depth = 0)
        {
            _minX = minX;
            _minY = minY;
            _maxX = maxX;
            _maxY = maxY;
            _depth = depth;
        }

        public void Insert(SegmentEndpoint item)
        {
            if (_children != null)
            {
                ChildFor(item.Point).Insert(item);
                return;
            }

            _items.Add(item);
            if (_items.Count <= NodeCapacity || _depth >= MaximumDepth)
            {
                return;
            }

            Subdivide();
            foreach (var existing in _items)
            {
                ChildFor(existing.Point).Insert(existing);
            }
            _items.Clear();
        }

        public void Query(double minX, double minY, double maxX, double maxY, ICollection<SegmentEndpoint> result)
        {
            if (maxX < _minX || minX > _maxX || maxY < _minY || minY > _maxY)
            {
                return;
            }

            if (_children != null)
            {
                foreach (var child in _children)
                {
                    child.Query(minX, minY, maxX, maxY, result);
                }
                return;
            }

            foreach (var item in _items)
            {
                if (item.Point.X >= minX && item.Point.X <= maxX
                    && item.Point.Y >= minY && item.Point.Y <= maxY)
                {
                    result.Add(item);
                }
            }
        }

        private void Subdivide()
        {
            var midX = (_minX + _maxX) / 2d;
            var midY = (_minY + _maxY) / 2d;
            _children = new[]
            {
                new EndpointQuadtree(_minX, _minY, midX, midY, _depth + 1),
                new EndpointQuadtree(midX, _minY, _maxX, midY, _depth + 1),
                new EndpointQuadtree(_minX, midY, midX, _maxY, _depth + 1),
                new EndpointQuadtree(midX, midY, _maxX, _maxY, _depth + 1)
            };
        }

        private EndpointQuadtree ChildFor(Point3d point)
        {
            var midX = (_minX + _maxX) / 2d;
            var midY = (_minY + _maxY) / 2d;
            var index = (point.X >= midX ? 1 : 0) + (point.Y >= midY ? 2 : 0);
            return _children![index];
        }
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
    public static List<Result> ScanWindow(
        Document document,
        Extents3d scanWindow,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null)
    {
        return ScanWindow(
            document,
            new CadSelectionWindow
            {
                Bounds = LocalRectangle.FromPoints(
                    scanWindow.MinPoint.X,
                    scanWindow.MinPoint.Y,
                    scanWindow.MaxPoint.X,
                    scanWindow.MaxPoint.Y),
                UcsToWorld = Matrix3d.Identity,
                WorldToUcs = Matrix3d.Identity
            },
            paperMatchToleranceMm,
            recognizeFourLineRectangles);
    }

    /// <summary>
    /// 按用户当前 UCS 中的矩形窗口扫描。Bounds 保持 UCS 原始宽高，WCS 只用于数据库实体读取。
    /// </summary>
    public static List<Result> ScanWindow(
        Document document,
        CadSelectionWindow scanWindow,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null)
    {
        LayerScannableCache.Clear();
        BlockDefinitionCache.Clear();
        var storedSettings = AppSettingsStore.Load();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
        var recognizeFourLines =
            recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
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

        var rectangles = CollectRectanglesFromSpace(tr, owner, recognizeFourLines);
        var ownerId = owner.ObjectId;
        var layoutName = layout.LayoutName;
        var layoutTabOrder = layout.TabOrder;
        var isPaperSpace = !layout.ModelType;
        tr.Commit();

        return FilterAndPackageRectangles(
            document,
            rectangles,
            scanWindow,
            ownerId,
            sourceFile,
            layoutName,
            isPaperSpace,
            layoutTabOrder,
            effectivePaperToleranceMm,
            storedSettings.LongPaperSnapToleranceMm,
            storedSettings.CustomScales);
    }

    /// <summary>
    /// 按扫描范围扫描多个布局中的矩形框。
    ///
    /// 遍历所有布局，按 <paramref name="scope"/> 决定扫描哪些空间，
    /// 每个空间独立收集矩形、过滤、打包为 Result。结果按布局遍历顺序排列。
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="scope">扫描范围</param>
    public static List<Result> ScanScope(
        Document document,
        TitleBlockScanScope scope,
        double? paperMatchToleranceMm = null,
        bool? recognizeFourLineRectangles = null)
    {
        LayerScannableCache.Clear();
        BlockDefinitionCache.Clear();
        var storedSettings = AppSettingsStore.Load();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
        var recognizeFourLines =
            recognizeFourLineRectangles ?? storedSettings.RecognizeFourLineRectangleFrames;
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

                var rectangles = CollectRectanglesFromSpace(tr, owner, recognizeFourLines);
                spaceData.Add((rectangles, owner.ObjectId, layout.LayoutName, !layout.ModelType, layout.TabOrder));
            }

            tr.Commit();
        }

        // 按布局 TabOrder 排序，确保模型空间在最前、图纸布局按选项卡顺序排列
        spaceData.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

        // 第二阶段：对每个空间独立过滤打包（FilterEmptyRectangles 内会开自己的事务）
        var allResults = new List<Result>();
        foreach (var (rectangles, ownerId, layoutName, isPaperSpace, tabOrder) in spaceData)
        {
            var results = FilterAndPackageRectangles(
                document,
                rectangles,
                isPaperSpace
                    ? null
                    : CadCoordinateSystem.CreateModelContext(document.Editor, true),
                ownerId,
                sourceFile,
                layoutName,
                isPaperSpace,
                tabOrder,
                effectivePaperToleranceMm,
                storedSettings.LongPaperSnapToleranceMm,
                storedSettings.CustomScales);
            allResults.AddRange(results);
        }

        return allResults;
    }

    // ═══════════════════════════════════════════════════════════════
    // 内部：单空间收集 & 过滤打包
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 遍历单个空间内的闭合 PL 矩形；开关开启时，同时收集顶层独立直线/直线型 PL，
    /// 经四叉树找出四边闭环后转换为同一个 LocalRectangle 流程。
    /// </summary>
    private static List<LocalRectangle> CollectRectanglesFromSpace(
        Transaction tr,
        BlockTableRecord owner,
        bool recognizeFourLineRectangles)
    {
        var rectangles = new List<LocalRectangle>();
        var segments = recognizeFourLineRectangles ? new List<LineSegment>() : null;
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            CollectEntityRectangles(
                tr,
                entity,
                Matrix3d.Identity,
                rectangles,
                segments,
                new HashSet<ObjectId>(),
                0,
                recognizeFourLineRectangles);
        }

        if (segments != null && segments.Count >= 4)
        {
            rectangles.AddRange(FindRectanglesFromSegments(segments));
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
        CadSelectionWindow? coordinateContext,
        ObjectId ownerId,
        string sourceFile,
        string layoutName,
        bool isPaperSpace,
        int layoutTabOrder,
        double paperMatchToleranceMm,
        double longPaperSnapToleranceMm,
        IReadOnlyList<double>? customScales = null)
    {
        // 3a. 窗口裁剪（可选）
        List<LocalRectangle> inWindow;
        if (coordinateContext != null && coordinateContext.Bounds.HasArea())
        {
            inWindow = rectangles
                .Where(rectangle => coordinateContext.IntersectsWorldPoints(GetWorldPoints(rectangle)))
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
        var coordinateBoundsByRect = new Dictionary<LocalRectangle, LocalRectangle>();
        foreach (var rectangle in inWindow)
        {
            var coordinateBounds = !isPaperSpace
                                   && coordinateContext != null
                                   && !coordinateContext.IsWorldCoordinateSystem
                ? coordinateContext.TransformWorldPointsToBounds(GetWorldPoints(rectangle))
                : null;
            var width = coordinateBounds != null
                ? coordinateBounds.MaxX - coordinateBounds.MinX
                : rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = coordinateBounds != null
                ? coordinateBounds.MaxY - coordinateBounds.MinY
                : rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            // 短边按设置中的毫米容差匹配常用比例；长边先吸附 1/8 模数，超出同一容差才转任意动态纸张。
            var detectionOptions = PaperSizeDetector.CreateRectangleBatchOptions(paperMatchToleranceMm, isPaperSpace, longPaperSnapToleranceMm, customScales);
            var options = PaperSizeDetector.DetectCandidates(width, height, detectionOptions);
            if (options.Count == 0)
            {
                continue;
            }

            paperMatched.Add(rectangle);
            paperOptionsByRect[rectangle] = options;
            if (coordinateBounds != null)
            {
                coordinateBoundsByRect[rectangle] = coordinateBounds;
            }
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
            coordinateBoundsByRect.TryGetValue(rectangle, out var coordinateBounds);
            var width = coordinateBounds != null
                ? coordinateBounds.MaxX - coordinateBounds.MinX
                : rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = coordinateBounds != null
                ? coordinateBounds.MaxY - coordinateBounds.MinY
                : rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            var index = results.Count;
            var result = new Result
            {
                CornerPoints = rectangle.CornerPoints,
                PaperOptions = options,
                Job = new PlotJob
                {
                    IsManualWindow = true,
                    SourceFile = sourceFile,
                    SpaceName = layoutName,
                    IsPaperSpace = isPaperSpace,
                    LayoutTabOrder = layoutTabOrder,
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
                        : (double[])rectangle.CornerPoints.Clone(),
                    FrameBoundaryHandles = rectangle.BoundaryEntityHandles == null
                        ? null
                        : (string[])rectangle.BoundaryEntityHandles.Clone()
                }
            };
            if (!isPaperSpace && coordinateContext != null && coordinateBounds != null)
            {
                coordinateContext.ApplyToJob(result.Job, coordinateBounds);
            }

            results.Add(result);
        }

        return results;
    }

    private static Point3d[] GetWorldPoints(LocalRectangle rectangle)
    {
        if (rectangle.CornerPoints is { Length: >= 8 } points)
        {
            return new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
        }

        return CadSelectionWindow.GetCorners(rectangle);
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
    /// <param name="recognizeFourLineRectangles">是否启用四个独立边实体组成矩形的识别</param>
    private static void CollectEntityRectangles(
        Transaction tr,
        Entity entity,
        Matrix3d transform,
        ICollection<LocalRectangle> rectangles,
        ICollection<LineSegment>? segments,
        ISet<ObjectId> visitedDefinitions,
        int depth,
        bool recognizeFourLineRectangles)
    {
        // 跳过标注图层——避免把自己的标注当矩形扫进去
        if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ── 分支 0：Line → 收集线段（仅块内部，最多 3 层）──
        if (segments != null
            && depth <= 3
            && entity is Line line
            && IsEntityVisible(entity)
            && IsFrameBoundaryLayerScannable(tr, entity))
        {
            var segment = new LineSegment
            {
                Start = line.StartPoint.TransformBy(transform),
                End = line.EndPoint.TransformBy(transform),
                EntityHandle = line.Handle.ToString()
            };
            if (segment.Start.DistanceTo(segment.End) > 1e-6)
            {
                segments.Add(segment);
            }
        }

        // ── 分支 0.5：开放直线型 PL → 每个实体只提取首尾端点作为一条矩形边 ──
        if (segments != null
            && depth <= 3
            && entity is Polyline plSegment
            && IsEntityVisible(entity)
            && IsFrameBoundaryLayerScannable(tr, entity)
            && TryGetStraightOpenPolylineSegment(plSegment, transform, out var polylineSegment))
        {
            polylineSegment.EntityHandle = plSegment.Handle.ToString();
            segments.Add(polylineSegment);
        }

        // 老式开放 POLYLINE 也按一个独立实体处理；仅允许全部顶点共线且无圆弧。
        if (segments != null
            && depth <= 3
            && entity is Polyline2d pl2dSegment
            && IsEntityVisible(entity)
            && IsFrameBoundaryLayerScannable(tr, entity)
            && TryGetStraightOpenPolyline2dSegment(
                tr,
                pl2dSegment,
                transform,
                out var legacyPolylineSegment))
        {
            legacyPolylineSegment.EntityHandle = pl2dSegment.Handle.ToString();
            segments.Add(legacyPolylineSegment);
        }

        // ── 分支 1：Polyline（轻量线）→ 矩形检测 ──
        if (entity is Polyline polyline
            // 图框 PL 可能专门放在 Defpoints 或其他”不打印”辅助层；
            // 它只提供打印边界，不代表该层图素会进入打印内容，因此这里只要求图层开启且未冻结。
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangle(
                polyline,
                transform,
                requireClosed: true,
                out var rectangle))
        {
            rectangle.BoundaryEntityHandles = new[] { polyline.Handle.ToString() };
            // 先全部收集，不去重——不同实例的同一定义各自独立，去重放在后续 FilterRectangles
            rectangles.Add(rectangle);
        }

        // ── 分支 1b：Polyline2d（老式 POLYLINE+VERTEX）→ 矩形检测 ──
        // 旧版 CAD 绘制的图框常用老式多段线，LIST 命令输出 “POLYLINE/VERTEX”，
        // .NET API 类型为 Polyline2d，与轻量 Polyline 不同，需单独处理。
        if (entity is Polyline2d polyline2d
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangleFrom2d(
                tr,
                polyline2d,
                transform,
                requireClosed: false,
                out var rectangle2d))
        {
            rectangle2d.BoundaryEntityHandles = new[] { polyline2d.Handle.ToString() };
            rectangles.Add(rectangle2d);
        }

        // ── 分支 1c：Polyline3d（3DPOLY）→ 矩形检测 ──
        // 三维多段线在 XY 平面上也可构成矩形图框，顶点类型为 Vertex3d。
        if (entity is Polyline3d polyline3d
            && IsFrameBoundaryLayerScannable(tr, entity)
            && RectangleGeometry.TryGetRectangleFrom3d(
                tr,
                polyline3d,
                transform,
                requireClosed: false,
                out var rectangle3d))
        {
            rectangle3d.BoundaryEntityHandles = new[] { polyline3d.Handle.ToString() };
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
                rectangles.Add(RectangleGeometry.TransformRectangle(cachedRects[0], instanceXform));
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
            // 四叉树负责限制邻域搜索成本，因此不再用固定 200 条上限截断复杂块。
            var localRects = new List<LocalRectangle>();
            var blockSegments = recognizeFourLineRectangles && depth < 3
                ? new List<LineSegment>()
                : null;
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity nested)
                {
                    continue;
                }

                // 递归阶段不能按“是否打印”提前截断，否则块内不可打印层上的图框 PL
                // 永远到不了下面的矩形检测分支。图框边实体只要求图层可见，内容输出仍严格检查可打印性。
                if (!IsEntityLayerVisibleForScanning(tr, nested))
                {
                    continue;
                }

                if (!IsEntityVisible(nested))
                {
                    continue;
                }

                // 用单位矩阵递归 → 子实体结果均在当前块定义局部坐标下
                CollectEntityRectangles(
                    tr,
                    nested,
                    Matrix3d.Identity,
                    localRects,
                    blockSegments,
                    visitedDefinitions,
                    depth + 1,
                    recognizeFourLineRectangles);
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
                rectangles.Add(RectangleGeometry.TransformRectangle(largest, instanceXform));
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

    /// <summary>
    /// 将一个开放轻量 PL 作为“一条边”提取。允许中间有冗余共线顶点，
    /// 但禁止圆弧、折线和沿原路回折，确保四个实体恰好对应矩形四条边。
    /// </summary>
    private static bool TryGetStraightOpenPolylineSegment(
        Polyline polyline,
        Matrix3d transform,
        out LineSegment segment)
    {
        segment = new LineSegment();
        if (polyline.Closed || polyline.NumberOfVertices < 2)
        {
            return false;
        }

        var points = new List<Point3d>(polyline.NumberOfVertices);
        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            if (index < polyline.NumberOfVertices - 1
                && Math.Abs(polyline.GetBulgeAt(index)) > 1e-9)
            {
                return false;
            }
            points.Add(polyline.GetPoint3dAt(index).TransformBy(transform));
        }

        return TryBuildStraightSegment(points, out segment);
    }

    /// <summary>老式开放 POLYLINE 的直线边提取，与轻量 PL 使用相同的共线和单调校验。</summary>
    private static bool TryGetStraightOpenPolyline2dSegment(
        Transaction tr,
        Polyline2d polyline,
        Matrix3d transform,
        out LineSegment segment)
    {
        segment = new LineSegment();
        if (polyline.Closed)
        {
            return false;
        }

        var vertices = new List<Vertex2d>();
        foreach (ObjectId vertexId in polyline)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead, false) is Vertex2d vertex)
            {
                vertices.Add(vertex);
            }
        }
        if (vertices.Count < 2)
        {
            return false;
        }

        var points = new List<Point3d>(vertices.Count);
        for (var index = 0; index < vertices.Count; index++)
        {
            if (index < vertices.Count - 1 && Math.Abs(vertices[index].Bulge) > 1e-9)
            {
                return false;
            }
            points.Add(vertices[index].Position.TransformBy(transform));
        }

        return TryBuildStraightSegment(points, out segment);
    }

    private static bool TryBuildStraightSegment(IReadOnlyList<Point3d> points, out LineSegment segment)
    {
        segment = new LineSegment();
        var start = points[0];
        var end = points[points.Count - 1];
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-12)
        {
            return false;
        }

        var length = Math.Sqrt(lengthSquared);
        var distanceTolerance = Math.Max(1e-6, length * 1e-8);
        var parameterTolerance = distanceTolerance / length;
        var previousParameter = -parameterTolerance;
        foreach (var point in points)
        {
            // 点到首尾直线的垂距必须接近 0。
            var cross = (point.X - start.X) * dy - (point.Y - start.Y) * dx;
            if (Math.Abs(cross) / length > distanceTolerance)
            {
                return false;
            }

            // 顶点必须沿首点→尾点单调前进，禁止一条 PL 在同一直线上回折或超出首尾端点。
            var parameter = ((point.X - start.X) * dx + (point.Y - start.Y) * dy) / lengthSquared;
            if (parameter < -parameterTolerance
                || parameter > 1d + parameterTolerance
                || parameter + parameterTolerance < previousParameter)
            {
                return false;
            }
            previousParameter = parameter;
        }

        segment = new LineSegment { Start = start, End = end };
        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // 四线段拼合矩形检测
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从独立线段集合中找出由 4 条线段首尾连接而成的矩形。
    ///
    /// 算法：
    ///   1. 将全部端点放入四叉树，以角点附近的小范围查询代替全量两两比较
    ///   2. 图遍历：从每条线段出发，沿连接关系走 4 步，找回到起点的闭环
    ///   3. 几何验证：直角、对边平行等长、对角线等长且中点重合
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

        // 只容纳浮点计算噪声，不允许用较大容差把实际有缝隙的四条线“吸”成闭环。
        const double endpointTolerance = 0.000001;
        const int maximumConnectionsAtCorner = 12;

        // ── 第 1 步：把端点写入四叉树 ──
        var minX = segments.Min(segment => Math.Min(segment.Start.X, segment.End.X));
        var minY = segments.Min(segment => Math.Min(segment.Start.Y, segment.End.Y));
        var maxX = segments.Max(segment => Math.Max(segment.Start.X, segment.End.X));
        var maxY = segments.Max(segment => Math.Max(segment.Start.Y, segment.End.Y));
        var padding = Math.Max(endpointTolerance, Math.Max(maxX - minX, maxY - minY) * 1e-12);
        var endpointTree = new EndpointQuadtree(
            minX - padding,
            minY - padding,
            maxX + padding,
            maxY + padding);
        for (var i = 0; i < segments.Count; i++)
        {
            endpointTree.Insert(new SegmentEndpoint { Point = segments[i].Start, SegmentIndex = i });
            endpointTree.Insert(new SegmentEndpoint { Point = segments[i].End, SegmentIndex = i });
        }

        // ── 第 2 步：用四叉树查询与指定点首尾相连的线段 ──
        bool IsNear(Point3d a, Point3d b, double tol)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            return dx * dx + dy * dy + dz * dz <= tol * tol;
        }

        List<int> FindConnected(ISet<int> excludedIndices, Point3d point)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            var endpoints = new List<SegmentEndpoint>();
            endpointTree.Query(
                point.X - endpointTolerance,
                point.Y - endpointTolerance,
                point.X + endpointTolerance,
                point.Y + endpointTolerance,
                endpoints);
            foreach (var endpoint in endpoints)
            {
                var segmentIndex = endpoint.SegmentIndex;
                if (excludedIndices.Contains(segmentIndex) || !seen.Add(segmentIndex))
                {
                    continue;
                }

                var segment = segments[segmentIndex];
                if (IsNear(segment.Start, point, endpointTolerance)
                    || IsNear(segment.End, point, endpointTolerance))
                {
                    result.Add(segmentIndex);
                    // 密集汇聚点容易造成组合爆炸，也不符合常规图框角点特征。
                    if (result.Count > maximumConnectionsAtCorner)
                    {
                        return result;
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

        // ── 第 3 步：沿端点连接关系只走 4 条边，并要求第 4 条边回到第 1 条边起点 ──
        var foundKeys = new HashSet<string>();

        void ExploreCycle(int i1, Point3d pointA, Point3d pointB)
        {
            var connectedAtB = FindConnected(new HashSet<int> { i1 }, pointB);
            if (connectedAtB.Count > maximumConnectionsAtCorner)
            {
                return;
            }

            foreach (var i2 in connectedAtB)
            {
                var pointC = OtherEnd(i2, pointB);
                var usedAfterSecond = new HashSet<int> { i1, i2 };
                var connectedAtC = FindConnected(usedAfterSecond, pointC);
                if (connectedAtC.Count > maximumConnectionsAtCorner)
                {
                    continue;
                }

                foreach (var i3 in connectedAtC)
                {
                    var pointD = OtherEnd(i3, pointC);
                    var usedAfterThird = new HashSet<int> { i1, i2, i3 };
                    var connectedAtD = FindConnected(usedAfterThird, pointD);
                    if (connectedAtD.Count > maximumConnectionsAtCorner)
                    {
                        continue;
                    }

                    foreach (var i4 in connectedAtD)
                    {
                        var backToA = OtherEnd(i4, pointD);
                        if (!IsNear(backToA, pointA, endpointTolerance))
                        {
                            continue;
                        }

                        var corners = new[] { pointA, pointB, pointC, pointD };
                        if (!TryBuildRectangleFromCorners(corners, out var rectangle))
                        {
                            continue;
                        }

                        // 防重复：同四个独立实体无论从哪条边、哪个方向出发，只生成一个矩形。
                        var ids = new[] { i1, i2, i3, i4 };
                        Array.Sort(ids);
                        var key = string.Join(",", ids);
                        if (!foundKeys.Add(key))
                        {
                            continue;
                        }

                        rectangle.BoundaryEntityHandles = ids
                            .Select(index => segments[index].EntityHandle)
                            .Where(handle => !string.IsNullOrWhiteSpace(handle))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        rectangles.Add(rectangle);
                    }
                }
            }
        }

        for (var i1 = 0; i1 < segments.Count; i1++)
        {
            var first = segments[i1];
            // 实体绘制方向任意，必须从两个方向各走一次才能保证不漏掉闭环。
            ExploreCycle(i1, first.Start, first.End);
            ExploreCycle(i1, first.End, first.Start);
        }

        return rectangles;
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
        if (corners.Max(point => point.Z) - corners.Min(point => point.Z) > tolerance)
        {
            return false;
        }

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

        var edgeX = new double[4];
        var edgeY = new double[4];
        var edgeLength = new double[4];
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            edgeX[index] = corners[next].X - corners[index].X;
            edgeY[index] = corners[next].Y - corners[index].Y;
            edgeLength[index] = Math.Sqrt(
                edgeX[index] * edgeX[index] + edgeY[index] * edgeY[index]);
            if (edgeLength[index] <= tolerance)
            {
                return false;
            }
        }

        const double directionTolerance = 0.001;
        for (var index = 0; index < 4; index++)
        {
            var next = (index + 1) % 4;
            var normalizedDot = Math.Abs(
                edgeX[index] * edgeX[next] + edgeY[index] * edgeY[next])
                / (edgeLength[index] * edgeLength[next]);
            if (normalizedDot > directionTolerance)
            {
                return false;
            }
        }

        // 两组对边必须分别平行、等长；可排除自交蝴蝶形和近似梯形。
        var parallel02 = Math.Abs(edgeX[0] * edgeY[2] - edgeY[0] * edgeX[2])
                         / (edgeLength[0] * edgeLength[2]);
        var parallel13 = Math.Abs(edgeX[1] * edgeY[3] - edgeY[1] * edgeX[3])
                         / (edgeLength[1] * edgeLength[3]);
        if (parallel02 > directionTolerance
            || parallel13 > directionTolerance
            || Math.Abs(edgeLength[0] - edgeLength[2]) > Math.Max(tolerance, edgeLength[0] * 0.001)
            || Math.Abs(edgeLength[1] - edgeLength[3]) > Math.Max(tolerance, edgeLength[1] * 0.001))
        {
            return false;
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

    /// <summary>四条独立线闭环验证使用的点重合判断；多段线矩形算法由 RectangleGeometry 负责。</summary>
    private static bool SamePoint(Point3d a, Point3d b, double tolerance)
    {
        return Math.Abs(a.X - b.X) <= tolerance
            && Math.Abs(a.Y - b.Y) <= tolerance;
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
    /// 图框边界的图层过滤规则：图层必须开启且未冻结，但允许设置为“不打印”。
    /// 不打印属性只决定图素是否输出，不应阻止闭合 PL 或四个独立边实体作为打印边界。
    /// </summary>
    private static bool IsFrameBoundaryLayerScannable(Transaction tr, Entity entity)
    {
        return IsEntityLayerVisibleForScanning(tr, entity);
    }

    /// <summary>
    /// 判断图层在当前图形中是否可见，仅检查关闭/冻结状态，不检查 IsPlottable。
    /// 此规则只用于查找图框边界和递归进入块定义；内容过滤仍调用严格的
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

    // 多段线矩形判定与角点变换统一由 RectangleGeometry 提供，扫描器仅保留扫描策略。
    // ═══════════════════════════════════════════════════════════════
    // 矩形过滤：去重、去嵌套
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 过滤矩形列表：去重 + 去嵌套。
    ///
    /// 算法：
    ///   1. 按面积降序排列（先处理大的）
    ///   2. 去重：两个矩形边界相同 或 高度重叠（>90%），只保留第一个
    ///   3. 去嵌套：小矩形完全被大矩形包含时移除小的，避免同一区域重复打印
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

        // 去嵌套：任何候选打印框只要被另一个候选框完整包含，就舍弃内部框。
        // 外框已经覆盖该打印区域，不能再让内框生成第二个打印任务。
        return unique
            .Where(candidate => !unique.Any(container =>
                !ReferenceEquals(container, candidate)
                && Area(container) > Area(candidate)
                && Contains(container, candidate)))
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
