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
/// 矩形框扫描器：在当前布局中递归扫描所有 Polyline 矩形，
/// 筛选出符合标准纸张比例的作为待打印图框。
///
/// 核心流程：ScanWindow 入口 → CollectEntityRectangles 递归遍历 →
/// TryGetRectangle 矩形检测 → FilterRectangles 去重去嵌套 → 生成 PlotJob。
///
/// 支持 WCS 和 UCS（旋转视图），矩形检测使用几何算法而非轴对齐检查。
/// </summary>
public static class RectangleFrameScanner
{
    /// <summary>临时序号标注图层名，扫描时跳过此图层的实体。</summary>
    private const string TemporaryOverlayLayer = "ZBP_TEMP_SEQUENCE_OVERLAY";

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
    /// 扫描指定窗口内的矩形框，每个匹配的矩形生成一个 PlotJob。
    ///
    /// 算法步骤：
    ///   1. 确定扫描空间（模型空间或当前图纸空间布局）
    ///   2. 遍历空间内所有顶层实体，递归进入块参照
    ///   3. 对找到的矩形做过滤去重
    ///   4. 每个矩形生成 PlotJob（坐标是 WCS，上游会转为 DCS）
    /// </summary>
    /// <param name="document">当前 CAD 文档</param>
    /// <param name="scanWindow">扫描窗口（WCS 坐标）</param>
    public static List<Result> ScanWindow(Document document, Extents3d scanWindow)
    {
        var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
            ? document.Name
            : document.Database.Filename;
        var rectangles = new List<LocalRectangle>();

        // ── 第 1 步：确定扫描目标空间 ──
        using var tr = document.Database.TransactionManager.StartTransaction();
        BlockTableRecord owner;
        Layout layout;
        if (document.Database.TileMode)
        {
            // 模型空间：从 BlockTable 中取 ModelSpace 记录
            var blockTable = (BlockTable)tr.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
            owner = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
        }
        else
        {
            // 图纸空间：用 LayoutManager 获取当前布局的 BlockTableRecord
            // 不用 CurrentSpaceId，因为用户可能在视口内编辑（CurrentSpaceId 指向模型空间）
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

        // ── 第 2 步：递归遍历空间内所有实体 ──
        var layoutName = layout.LayoutName;
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            // 从顶层实体开始，transform=Identity，depth=0
            // 不做纸张过滤，先收集所有矩形
            CollectEntityRectangles(tr, entity, Matrix3d.Identity, rectangles, new HashSet<ObjectId>(), 0);
        }

        tr.Commit();

        // ── 第 3 步：逐层过滤 ──
        // 3a. 按扫描窗口裁剪（第一道，减少后续处理量）
        var window = LocalRectangle.FromPoints(
            scanWindow.MinPoint.X,
            scanWindow.MinPoint.Y,
            scanWindow.MaxPoint.X,
            scanWindow.MaxPoint.Y);
        var inWindow = rectangles
            .Where(r => Intersects(r, window))
            .ToList();

        // 3b. 去重去嵌套（重叠的矩形只保留一个，嵌套的内外框去内留外）
        var unique = FilterRectangles(inWindow);

        // 3d. 纸张标准比例过滤（最后一道，只保留能匹配标准纸张尺寸的矩形）
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        var results = new List<Result>();
        foreach (var rectangle in unique)
        {
            var width = rectangle.ActualWidth > 0 ? rectangle.ActualWidth : rectangle.MaxX - rectangle.MinX;
            var height = rectangle.ActualHeight > 0 ? rectangle.ActualHeight : rectangle.MaxY - rectangle.MinY;
            var options = PaperSizeDetector.DetectCandidates(width, height);
            if (options.Count == 0)
            {
                continue; // 不匹配任何标准纸张 → 丢弃
            }

            var paper = options.First();
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
                    IsPaperSpace = !layout.ModelType,
                    DrawingNumber = (index + 1).ToString("D2"),
                    Title = stem,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{width:0.##} x {height:0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = "矩形框批量打印",
                    PaperWidthMm = paper.PaperWidthMm,
                    PaperHeightMm = paper.PaperHeightMm,
                    MinX = rectangle.MinX,
                    MinY = rectangle.MinY,
                    MaxX = rectangle.MaxX,
                    MaxY = rectangle.MaxY
                }
            });
        }

        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    // 递归实体遍历
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 递归遍历实体及其子实体（块定义内部），检测矩形 Polyline。
    ///
    /// 对每个实体：
    ///   1. 如果是 Polyline → 尝试检测矩形 → 加入结果
    ///   2. 如果是 BlockReference → 进入块定义递归
    ///
    /// 过滤规则：
    ///   - 跳过临时序号标注图层
    ///   - 跳过不可打印图层的实体
    ///   - 跳过 CAD 判定为不可见的实体（动态块隐藏状态等）
    ///   - 防循环：同一个块定义只处理一次（visitedDefinitions）
    ///   - 防过深：递归深度上限 12 层
    /// </summary>
    /// <param name="tr">事务</param>
    /// <param name="entity">当前实体</param>
    /// <param name="transform">从当前实体坐标系到 WCS 的累积变换矩阵</param>
    /// <param name="rectangles">收集到的矩形列表</param>
    /// <param name="visitedDefinitions">已访问的块定义 ID，防循环</param>
    /// <param name="depth">当前递归深度</param>
    private static void CollectEntityRectangles(
        Transaction tr,
        Entity entity,
        Matrix3d transform,
        ICollection<LocalRectangle> rectangles,
        ISet<ObjectId> visitedDefinitions,
        int depth)
    {
        // 跳过标注图层——避免把自己的标注当矩形扫进去
        if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // ── 分支 1：Polyline → 矩形检测 ──
        if (entity is Polyline polyline
            && IsEntityLayerScannable(tr, entity)
            && TryGetRectangle(polyline, transform, out var rectangle))
        {
            // 先全部收集，不去重——不同实例的同一定义各自独立，去重放在后续 FilterRectangles
            rectangles.Add(rectangle);
        }

        // ── 分支 2：BlockReference → 递归进入 ──
        if (entity is not BlockReference blockReference || depth >= 12)
        {
            return;
        }

        var definitionId = blockReference.BlockTableRecord;
        // 防循环：同一个块定义在一条递归路径上只进入一次
        if (!visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
            // 累积变换矩阵：子实体坐标 × blockRef 的变换 = 子实体的 WCS 坐标
            var nestedTransform = blockReference.BlockTransform * transform;

            // 同一块定义内只保留最大的矩形框（每个 BlockRef 实例独立遍历，互不影响）
            // 例如块定义内含一大一小两个嵌套矩形，只取大的
            var localRects = new List<LocalRectangle>();
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity nested)
                {
                    continue;
                }

                // 图层过滤：跳过关闭/冻结/不打印图层的实体
                if (!IsEntityLayerScannable(tr, nested))
                {
                    continue;
                }

                // 可见性过滤：用 CAD 引擎原生 entity.Visible 判断
                // 动态块切换可见性状态时 CAD 自动更新此属性，无需猜名字或图层
                if (!IsEntityVisible(nested))
                {
                    continue;
                }

                // 递归：子实体用累积的 transform 继续遍历
                // 矩形先收到 localRects，离开时只保留最大的加到父级
                CollectEntityRectangles(tr, nested, nestedTransform, localRects, visitedDefinitions, depth + 1);
            }

            // 该块定义内只保留面积最大的矩形，加到父级列表
            if (localRects.Count > 0)
            {
                rectangles.Add(localRects.OrderByDescending(Area).First());
            }
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
    /// </summary>
    private static bool IsEntityLayerScannable(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull
                || tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                return false;
            }

            return !layer.IsOff      // 图层未关闭
                && !layer.IsFrozen    // 图层未冻结
                && layer.IsPlottable; // 图层可打印
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

    /// <summary>两个轴对齐矩形是否相交。</summary>
    private static bool Intersects(LocalRectangle rectangle, LocalRectangle window)
    {
        return rectangle.MaxX >= window.MinX
            && rectangle.MinX <= window.MaxX
            && rectangle.MaxY >= window.MinY
            && rectangle.MinY <= window.MaxY;
    }
}
