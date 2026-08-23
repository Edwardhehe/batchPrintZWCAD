using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.DatabaseServices.Filters;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.DatabaseServices.Filters;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>集中处理拆图范围、坐标转换、实体相交及 XCLIP 判断。</summary>
internal static class DwgSplitGeometry
{
    internal static Extents3d BuildWindow(PlotJob job)
    {
        if (job.CornerPoints != null && job.CornerPoints.Length >= 8)
        {
            var xs = new[] { job.CornerPoints[0], job.CornerPoints[2], job.CornerPoints[4], job.CornerPoints[6] };
            var ys = new[] { job.CornerPoints[1], job.CornerPoints[3], job.CornerPoints[5], job.CornerPoints[7] };
            return new Extents3d(
                new Point3d(xs.Min(), ys.Min(), 0),
                new Point3d(xs.Max(), ys.Max(), 0));
        }

        return new Extents3d(
            new Point3d(Math.Min(job.MinX, job.MaxX), Math.Min(job.MinY, job.MaxY), 0),
            new Point3d(Math.Max(job.MinX, job.MaxX), Math.Max(job.MinY, job.MaxY), 0));
    }

    /// <summary>
    /// 拆图去留所用窗口。布局用纸空间 WCS；模型 UCS 与矩形框扫描相同：四个实际角点变到 UCS 后再取轴对齐盒，
    /// 禁止先取 WCS 包围盒再变换（旋转时约放大 √2）。
    /// </summary>
    internal static Extents3d BuildDecisionWindow(PlotJob job)
    {
        if (job.IsPaperSpace)
        {
            return BuildWindow(job);
        }

        if (job.UsesUserCoordinateSystem)
        {
            return ToExtents(CreateJobUcsContext(job).Bounds);
        }

        return BuildWindow(job);
    }

    /// <summary>
    /// 与 <see cref="CadSelectionWindow.TransformWorldPointsToBounds"/> 同一套 UCS 上下文：
    /// 有 <c>CornerPoints</c> 时用四个实际世界角点变换，不用 Min/Max 的 WCS 包围盒。
    /// </summary>
    private static CadSelectionWindow CreateJobUcsContext(PlotJob job)
    {
        var ucsToWorld = CadSelectionWindow.GetJobUcsToWorld(job);
        var context = new CadSelectionWindow
        {
            UcsToWorld = ucsToWorld,
            WorldToUcs = ucsToWorld.Inverse()
        };
        context.Bounds = job.CornerPoints is { Length: >= 8 }
            ? context.TransformWorldPointsToBounds(GetSplitWorldCorners(job))
            : LocalRectangle.FromPoints(job.UcsMinX, job.UcsMinY, job.UcsMaxX, job.UcsMaxY);
        return context;
    }

    /// <summary>拆图用的四个实际世界角点，与矩形框扫描 <c>GetWorldPoints</c> 同序。</summary>
    private static Point3d[] GetSplitWorldCorners(PlotJob job)
    {
        if (job.CornerPoints is { Length: >= 8 } points)
        {
            return new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
        }

        return CadSelectionWindow.GetJobWorldCorners(job);
    }

    private static Extents3d ToExtents(LocalRectangle rectangle)
    {
        return new Extents3d(
            new Point3d(rectangle.MinX, rectangle.MinY, 0),
            new Point3d(rectangle.MaxX, rectangle.MaxY, 0));
    }

    /// <summary>
    /// 拆图保留多边形（WCS 绕序四角）。
    /// UCS：UCS 矩形四角 × UCS→WCS，得到斜矩形，不用四角的轴对齐包围盒。
    /// 布局/WCS：用扫描得到的实际四角。
    /// </summary>
    /// <param name="job">拆图任务。</param>
    /// <returns>世界坐标下依次连接的四个角点。</returns>
    internal static Point3d[] BuildKeepPolygon(PlotJob job)
    {
        if (!job.IsPaperSpace && job.UsesUserCoordinateSystem)
        {
            var ucsToWorld = CadSelectionWindow.GetJobUcsToWorld(job);
            return new[]
            {
                new Point3d(job.UcsMinX, job.UcsMinY, 0),
                new Point3d(job.UcsMaxX, job.UcsMinY, 0),
                new Point3d(job.UcsMaxX, job.UcsMaxY, 0),
                new Point3d(job.UcsMinX, job.UcsMaxY, 0)
            }
            .Select(point => point.TransformBy(ucsToWorld))
            .ToArray();
        }

        if (job.CornerPoints is { Length: >= 8 } points)
        {
            return new[]
            {
                new Point3d(points[0], points[1], 0),
                new Point3d(points[2], points[3], 0),
                new Point3d(points[4], points[5], 0),
                new Point3d(points[6], points[7], 0)
            };
        }

        var minX = Math.Min(job.MinX, job.MaxX);
        var minY = Math.Min(job.MinY, job.MaxY);
        var maxX = Math.Max(job.MinX, job.MaxX);
        var maxY = Math.Max(job.MinY, job.MaxY);
        return new[]
        {
            new Point3d(minX, minY, 0),
            new Point3d(maxX, minY, 0),
            new Point3d(maxX, maxY, 0),
            new Point3d(minX, maxY, 0)
        };
    }

    /// <summary>点是否落在拆图保留多边形内（含边）。</summary>
    internal static bool IsPointInsideKeepPolygon(Point3d point, Point3d[] polygon)
    {
        return IsPointInsidePolygon(point, polygon);
    }

    /// <summary>
    /// 浮动视口是否属于当前图框：中心在内，或纸面视口矩形伸进图框内部。
    /// 只贴边的邻框视口不带；穿框视口必须留。
    /// </summary>
    internal static bool ViewportHitsKeepPolygon(Viewport viewport, Point3d[] polygon)
    {
        var center = viewport.CenterPoint;
        if (IsPointInsidePolygon(center, polygon))
        {
            return true;
        }

        try
        {
            var halfWidth = Math.Max(viewport.Width, 0) / 2.0;
            var halfHeight = Math.Max(viewport.Height, 0) / 2.0;
            var window = new Extents3d(
                new Point3d(center.X - halfWidth, center.Y - halfHeight, 0),
                new Point3d(center.X + halfWidth, center.Y + halfHeight, 0));
            return IntersectsRectangleAndPolygon(window, InsetPolygon(polygon, 0.02));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 另存副本后按当前图框去留。UCS 必须用斜矩形，禁止用四角 WCS 包盒。
    /// </summary>
    internal static bool ShouldKeepEntity(
        Transaction tr,
        Entity entity,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        try
        {
            if (TryGetXclipBoundary(tr, entity, out var clipPoints, out _))
            {
                return XclipFrameHitsPrintRange(job, clipPoints);
            }

            if (!job.IsPaperSpace && job.UsesUserCoordinateSystem)
            {
                return ShouldKeepByUcsRectangle(tr, entity, job);
            }

            var polygon = BuildKeepPolygon(job);
            if (IsAdjacentTitleFrame(entity, polygon))
            {
                return false;
            }

            return EntityHitsKeepPolygon(entity, polygon);
        }
        catch
        {
            result.UnknownExtentsKept++;
            return true;
        }
    }

    /// <summary>
    /// 在 UCS 内去留：窗口就是 UCS 矩形，图元从 WCS 变到 UCS 后再比。
    /// 不把四角转到 WCS，也不用四角包盒。
    /// </summary>
    private static bool ShouldKeepByUcsRectangle(Transaction tr, Entity entity, PlotJob job)
    {
        var worldToUcs = CadSelectionWindow.GetJobUcsToWorld(job).Inverse();
        var ucsRect = new Extents3d(
            new Point3d(Math.Min(job.UcsMinX, job.UcsMaxX), Math.Min(job.UcsMinY, job.UcsMaxY), 0),
            new Point3d(Math.Max(job.UcsMinX, job.UcsMaxX), Math.Max(job.UcsMinY, job.UcsMaxY), 0));

        if (IsAdjacentTitleFrameInUcs(entity, worldToUcs, ucsRect))
        {
            return false;
        }

        if (entity is Curve curve && CurveHitsUcsRectangle(curve, worldToUcs, ucsRect))
        {
            return true;
        }

        try
        {
            using var transformed = entity.GetTransformedCopy(worldToUcs);
            return Intersects(transformed.GeometricExtents, ucsRect);
        }
        catch
        {
            if (TryGetEntityReferencePoint(entity, out var worldPoint)
                && IsPointInside(ucsRect, worldPoint.TransformBy(worldToUcs)))
            {
                return true;
            }

            try
            {
                var ucsBounds = GetBounds(
                    GetRectangleCorners(entity.GeometricExtents)
                        .Select(point => point.TransformBy(worldToUcs)));
                return Intersects(ucsBounds, ucsRect);
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>曲线变到 UCS 后采样，或与 UCS 矩形四边求交。</summary>
    private static bool CurveHitsUcsRectangle(Curve source, Matrix3d worldToUcs, Extents3d ucsRect)
    {
        try
        {
            using var curve = (Curve)source.GetTransformedCopy(worldToUcs);
            var start = curve.StartParam;
            var end = curve.EndParam;
            if (!double.IsNaN(start) && !double.IsNaN(end)
                && !double.IsInfinity(start) && !double.IsInfinity(end))
            {
                const int sampleCount = 16;
                for (var index = 0; index <= sampleCount; index++)
                {
                    var parameter = start + (end - start) * index / sampleCount;
                    if (IsPointInside(ucsRect, curve.GetPointAtParameter(parameter)))
                    {
                        return true;
                    }
                }
            }

            var corners = GetRectangleCorners(ucsRect);
            for (var edge = 0; edge < corners.Length; edge++)
            {
                using var boundary = new Line(corners[edge], corners[(edge + 1) % corners.Length]);
                var intersections = new Point3dCollection();
                curve.IntersectWith(
                    boundary,
                    Intersect.OnBothOperands,
                    intersections,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (intersections.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            try
            {
                using var transformed = source.GetTransformedCopy(worldToUcs);
                return Intersects(transformed.GeometricExtents, ucsRect);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>只有图框类对象才做邻框排除，避免穿框填充/图像被当成邻框删掉。</summary>
    private static bool IsTitleFrameCandidate(Entity entity)
    {
        return entity is BlockReference
            || (entity is Polyline polyline && polyline.Closed)
            || (entity is Polyline2d polyline2d && polyline2d.Closed);
    }

    /// <summary>UCS 下的邻框：中心在 UCS 矩形外，且尺寸接近当前图框。</summary>
    private static bool IsAdjacentTitleFrameInUcs(Entity entity, Matrix3d worldToUcs, Extents3d ucsRect)
    {
        if (!IsTitleFrameCandidate(entity))
        {
            return false;
        }

        try
        {
            using var transformed = entity.GetTransformedCopy(worldToUcs);
            var extents = transformed.GeometricExtents;
            if (!IsSimilarSizedFrameOutside(extents, ucsRect))
            {
                return false;
            }

            return !Intersects(extents, InsetExtents(ucsRect, 0.02));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 紧邻图框：仅块或闭合多段线，中心在当前图框外且尺寸接近当前图框。
    /// </summary>
    private static bool IsAdjacentTitleFrame(Entity entity, Point3d[] polygon)
    {
        if (!IsTitleFrameCandidate(entity))
        {
            return false;
        }

        try
        {
            var extents = entity.GeometricExtents;
            var center = new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                0);
            if (IsPointInsidePolygon(center, polygon))
            {
                return false;
            }

            if (!IsSimilarSizedFrameOutside(extents, GetBounds(polygon)))
            {
                return false;
            }

            return !IntersectsRectangleAndPolygon(extents, InsetPolygon(polygon, 0.02));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>中心在窗外且尺寸接近窗口，视为紧邻图框。</summary>
    private static bool IsSimilarSizedFrameOutside(Extents3d extents, Extents3d window)
    {
        var center = new Point3d(
            (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
            (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
            0);
        if (IsPointInside(window, center))
        {
            return false;
        }

        var windowWidth = Math.Max(window.MaxPoint.X - window.MinPoint.X, 1e-9);
        var windowHeight = Math.Max(window.MaxPoint.Y - window.MinPoint.Y, 1e-9);
        var entityWidth = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, 0);
        var entityHeight = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, 0);
        return entityWidth >= windowWidth * 0.4
            && entityWidth <= windowWidth * 2.5
            && entityHeight >= windowHeight * 0.4
            && entityHeight <= windowHeight * 2.5;
    }

    /// <summary>
    /// 实体是否落在图框内或与图框相交。穿框图元中心可能在框外，仍必须保留。
    /// </summary>
    private static bool EntityHitsKeepPolygon(Entity entity, Point3d[] polygon)
    {
        if (entity is Curve curve && CurveHitsKeepPolygon(curve, polygon))
        {
            return true;
        }

        if (TryGetEntityReferencePoint(entity, out var referencePoint)
            && IsPointInsidePolygon(referencePoint, polygon))
        {
            return true;
        }

        try
        {
            var extents = entity.GeometricExtents;
            var center = new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                0);
            return IsPointInsidePolygon(center, polygon)
                || IntersectsRectangleAndPolygon(extents, polygon);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>把多边形向形心收缩，用来区分贴边邻框和真正穿入图框的内容。</summary>
    private static Point3d[] InsetPolygon(Point3d[] polygon, double factor)
    {
        if (polygon.Length == 0 || factor <= 0)
        {
            return polygon;
        }

        var centerX = polygon.Average(point => point.X);
        var centerY = polygon.Average(point => point.Y);
        return polygon
            .Select(point => new Point3d(
                point.X + (centerX - point.X) * factor,
                point.Y + (centerY - point.Y) * factor,
                0))
            .ToArray();
    }

    /// <summary>把轴对齐窗口向内收缩，用途同 <see cref="InsetPolygon"/>。</summary>
    private static Extents3d InsetExtents(Extents3d window, double factor)
    {
        var width = window.MaxPoint.X - window.MinPoint.X;
        var height = window.MaxPoint.Y - window.MinPoint.Y;
        var insetX = width * factor;
        var insetY = height * factor;
        if (insetX * 2 >= width || insetY * 2 >= height)
        {
            return window;
        }

        return new Extents3d(
            new Point3d(window.MinPoint.X + insetX, window.MinPoint.Y + insetY, 0),
            new Point3d(window.MaxPoint.X - insetX, window.MaxPoint.Y - insetY, 0));
    }

    /// <summary>曲线采样或与斜矩形四边求交。</summary>
    private static bool CurveHitsKeepPolygon(Curve curve, Point3d[] polygon)
    {
        try
        {
            var start = curve.StartParam;
            var end = curve.EndParam;
            if (!double.IsNaN(start) && !double.IsNaN(end)
                && !double.IsInfinity(start) && !double.IsInfinity(end))
            {
                const int sampleCount = 16;
                for (var index = 0; index <= sampleCount; index++)
                {
                    var parameter = start + (end - start) * index / sampleCount;
                    if (IsPointInsidePolygon(curve.GetPointAtParameter(parameter), polygon))
                    {
                        return true;
                    }
                }
            }

            for (var edge = 0; edge < polygon.Length; edge++)
            {
                using var boundary = new Line(polygon[edge], polygon[(edge + 1) % polygon.Length]);
                var intersections = new Point3dCollection();
                curve.IntersectWith(
                    boundary,
                    Intersect.OnBothOperands,
                    intersections,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (intersections.Count > 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // 再退回包盒与多边形相交。
        }

        return false;
    }

    /// <summary>
    /// UCS 去留：图框四角只变换一次，得到 WCS 中的真实斜矩形；实体几何临时变换到该矩形的
    /// 局部坐标中做精确相交。不能再拿实体的 WCS 轴对齐包盒与斜框判断，否则相邻图素仍会误入。
    /// </summary>
    private static bool ShouldKeepEntityInJobUcs(
        Transaction tr,
        Entity entity,
        Extents3d ucsWindow,
        PlotJob job,
        DwgSplitService.SplitResult result)
    {
        var ucs = CreateJobUcsContext(job);
        var worldToUcs = ucs.WorldToUcs;
        if (TryGetXclipBoundary(tr, entity, out var clipPoints, out var inverted))
        {
            var clipPointsInUcs = clipPoints
                .Select(point => point.TransformBy(worldToUcs))
                .ToArray();
            var entityBoundsInUcs = TryGetTransformedExtents(entity, worldToUcs, out var transformedBounds)
                ? transformedBounds
                : GetBounds(clipPointsInUcs);
            return XclipVisibleRegionIntersects(
                ucsWindow,
                entityBoundsInUcs,
                clipPointsInUcs,
                inverted);
        }

        var relation = ClassifyEntityAgainstUcsWindow(tr, entity, worldToUcs, ucsWindow, 0);
        if (relation == WindowRelation.Unknown)
        {
            // 真正无法读取的代理/自定义对象才保守保留；普通曲线和块不会再因 WCS 包盒过大而进入这里。
            result.UnknownExtentsKept++;
            return true;
        }

        return relation == WindowRelation.Intersects;
    }

    private enum WindowRelation
    {
        Outside,
        Intersects,
        Unknown
    }

    /// <summary>
    /// 把实体的真实几何临时变换到图框局部坐标。源实体和输出坐标均不改变；这里只用于去留判定。
    /// 块必须检查展开后的子图元，不能只看整个块的包盒。
    /// </summary>
    private static WindowRelation ClassifyEntityAgainstUcsWindow(
        Transaction tr,
        Entity entity,
        Matrix3d worldToUcs,
        Extents3d ucsWindow,
        int depth)
    {
        const int maxBlockDepth = 8;
        // 只用于快速排除：WCS 包盒变到 UCS 后只会变大，不会漏掉真正与斜框相交的实体。
        // 包盒仍重叠时必须继续检查真实曲线/块几何，不能把粗筛结果当最终结论。
        if (TryGetWorldExtentsBoundsInUcs(entity, worldToUcs, out var coarseBounds)
            && !Intersects(coarseBounds, ucsWindow))
        {
            return WindowRelation.Outside;
        }

        if (entity is Curve curve)
        {
            var curveRelation = ClassifyCurveAgainstUcsWindow(curve, worldToUcs, ucsWindow);
            if (curveRelation != WindowRelation.Unknown)
            {
                return curveRelation;
            }
        }

        if (entity is BlockReference blockReference && depth < maxBlockDepth)
        {
            var blockRelation = ClassifyBlockAgainstUcsWindow(
                tr,
                blockReference,
                worldToUcs,
                ucsWindow,
                depth + 1);
            if (blockRelation != WindowRelation.Unknown)
            {
                return blockRelation;
            }
        }

        if (TryGetTransformedExtents(entity, worldToUcs, out var localExtents))
        {
            return Intersects(localExtents, ucsWindow)
                ? WindowRelation.Intersects
                : WindowRelation.Outside;
        }

        return TryGetEntityReferencePoint(entity, out var worldPoint)
            ? (IsPointInside(ucsWindow, worldPoint.TransformBy(worldToUcs))
                ? WindowRelation.Intersects
                : WindowRelation.Outside)
            : WindowRelation.Unknown;
    }

    private static WindowRelation ClassifyBlockAgainstUcsWindow(
        Transaction tr,
        BlockReference blockReference,
        Matrix3d worldToUcs,
        Extents3d ucsWindow,
        int depth)
    {
        var exploded = new DBObjectCollection();
        try
        {
            blockReference.Explode(exploded);
            var foundGeometry = false;
            var hasUnknown = false;
            foreach (DBObject item in exploded)
            {
                if (item is not Entity child)
                {
                    continue;
                }

                foundGeometry = true;
                var relation = ClassifyEntityAgainstUcsWindow(tr, child, worldToUcs, ucsWindow, depth);
                if (relation == WindowRelation.Intersects)
                {
                    return WindowRelation.Intersects;
                }

                hasUnknown |= relation == WindowRelation.Unknown;
            }

            if (foundGeometry)
            {
                return hasUnknown ? WindowRelation.Unknown : WindowRelation.Outside;
            }
        }
        catch
        {
            // 某些动态块/代理块不能 Explode；下面再尝试读取块定义。
        }
        finally
        {
            foreach (DBObject item in exploded)
            {
                item.Dispose();
            }
        }

        return ClassifyBlockDefinitionAgainstUcsWindow(
            tr,
            blockReference,
            worldToUcs,
            ucsWindow,
            depth);
    }

    /// <summary>代理块不能展开时，从块定义克隆临时子实体并套用块变换。</summary>
    private static WindowRelation ClassifyBlockDefinitionAgainstUcsWindow(
        Transaction tr,
        BlockReference blockReference,
        Matrix3d worldToUcs,
        Extents3d ucsWindow,
        int depth)
    {
        try
        {
            var definition = (BlockTableRecord)tr.GetObject(
                blockReference.BlockTableRecord,
                OpenMode.ForRead,
                false);
            var foundGeometry = false;
            var hasUnknown = false;
            foreach (ObjectId childId in definition)
            {
                if (childId.IsErased
                    || tr.GetObject(childId, OpenMode.ForRead, false) is not Entity definitionEntity)
                {
                    continue;
                }

                using var child = definitionEntity.Clone() as Entity;
                if (child == null)
                {
                    hasUnknown = true;
                    continue;
                }

                foundGeometry = true;
                child.TransformBy(blockReference.BlockTransform);
                var relation = ClassifyEntityAgainstUcsWindow(tr, child, worldToUcs, ucsWindow, depth);
                if (relation == WindowRelation.Intersects)
                {
                    return WindowRelation.Intersects;
                }

                hasUnknown |= relation == WindowRelation.Unknown;
            }

            if (foundGeometry)
            {
                return hasUnknown ? WindowRelation.Unknown : WindowRelation.Outside;
            }
        }
        catch
        {
            // 保留 Unknown 交给上层采用安全策略，不能因单个自定义块中止整张拆图。
        }

        return WindowRelation.Unknown;
    }

    private static WindowRelation ClassifyCurveAgainstUcsWindow(
        Curve source,
        Matrix3d worldToUcs,
        Extents3d ucsWindow)
    {
        try
        {
            using var curve = (Curve)source.GetTransformedCopy(worldToUcs);
            // 端点/参数采样用于识别完全位于框内的闭合曲线；边界求交负责识别穿框曲线。
            var start = curve.StartParam;
            var end = curve.EndParam;
            if (!double.IsNaN(start) && !double.IsNaN(end)
                && !double.IsInfinity(start) && !double.IsInfinity(end))
            {
                const int sampleCount = 16;
                for (var index = 0; index <= sampleCount; index++)
                {
                    var parameter = start + (end - start) * index / sampleCount;
                    if (IsPointInside(ucsWindow, curve.GetPointAtParameter(parameter)))
                    {
                        return WindowRelation.Intersects;
                    }
                }
            }

            var corners = GetRectangleCorners(ucsWindow);
            for (var edge = 0; edge < corners.Length; edge++)
            {
                using var boundary = new Line(corners[edge], corners[(edge + 1) % corners.Length]);
                var intersections = new Point3dCollection();
                curve.IntersectWith(
                    boundary,
                    Intersect.OnBothOperands,
                    intersections,
                    IntPtr.Zero,
                    IntPtr.Zero);
                if (intersections.Count > 0)
                {
                    return WindowRelation.Intersects;
                }
            }

            return WindowRelation.Outside;
        }
        catch
        {
            return WindowRelation.Unknown;
        }
    }

    /// <summary>
    /// 先变换实体本身再读取包盒。与“先读取 WCS 包盒、再变换包盒四角”不同，不会产生二次放大。
    /// </summary>
    private static bool TryGetTransformedExtents(
        Entity entity,
        Matrix3d transform,
        out Extents3d extents)
    {
        try
        {
            using var transformed = entity.GetTransformedCopy(transform);
            extents = transformed.GeometricExtents;
            return true;
        }
        catch
        {
            extents = default;
            return false;
        }
    }

    private static bool TryGetWorldExtentsBoundsInUcs(
        Entity entity,
        Matrix3d worldToUcs,
        out Extents3d bounds)
    {
        try
        {
            bounds = GetBounds(GetRectangleCorners(entity.GeometricExtents)
                .Select(point => point.TransformBy(worldToUcs)));
            return true;
        }
        catch
        {
            bounds = default;
            return false;
        }
    }

    private static bool TryGetEntityReferencePoint(Entity entity, out Point3d point)
    {
        switch (entity)
        {
            case DBPoint dbPoint:
                point = dbPoint.Position;
                return true;
            case DBText text:
                point = text.Position;
                return true;
            case MText text:
                point = text.Location;
                return true;
            case BlockReference blockReference:
                point = blockReference.Position;
                return true;
            default:
                point = Point3d.Origin;
                return false;
        }
    }

    /// <summary>
    /// 轴对齐窗与 XCLIP 边界的真实二维相交。边界可以是凹多边形，因此不能用凸多边形 SAT。
    /// </summary>
    private static bool IntersectsRectangleAndPolygon(Extents3d rectangle, Point3d[] polygon)
    {
        if (polygon.Length < 3 || !Intersects(rectangle, GetBounds(polygon)))
        {
            return false;
        }

        if (polygon.Any(point => IsPointInside(rectangle, point)))
        {
            return true;
        }

        var corners = GetRectangleCorners(rectangle);
        if (corners.Any(point => IsPointInsidePolygon(point, polygon)))
        {
            return true;
        }

        for (var i = 0; i < polygon.Length; i++)
        {
            var polygonStart = polygon[i];
            var polygonEnd = polygon[(i + 1) % polygon.Length];
            for (var edge = 0; edge < corners.Length; edge++)
            {
                if (SegmentsIntersect(
                        polygonStart,
                        polygonEnd,
                        corners[edge],
                        corners[(edge + 1) % corners.Length]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// XCLIP 块只看裁剪框：裁剪框与图框打印范围相交即保留，不看块插入点或未裁剪外包。
    /// </summary>
    private static bool XclipFrameHitsPrintRange(PlotJob job, Point3d[] clipWorldPoints)
    {
        if (clipWorldPoints.Length < 2)
        {
            return false;
        }

        if (!job.IsPaperSpace && job.UsesUserCoordinateSystem)
        {
            var worldToUcs = CadSelectionWindow.GetJobUcsToWorld(job).Inverse();
            var clipInUcs = clipWorldPoints.Select(point => point.TransformBy(worldToUcs)).ToArray();
            var ucsRect = new Extents3d(
                new Point3d(Math.Min(job.UcsMinX, job.UcsMaxX), Math.Min(job.UcsMinY, job.UcsMaxY), 0),
                new Point3d(Math.Max(job.UcsMinX, job.UcsMaxX), Math.Max(job.UcsMinY, job.UcsMaxY), 0));
            return IntersectsRectangleAndPolygon(ucsRect, clipInUcs);
        }

        return PolygonsIntersect(BuildKeepPolygon(job), clipWorldPoints);
    }

    /// <summary>两个多边形是否相交或互相包含。</summary>
    private static bool PolygonsIntersect(Point3d[] first, Point3d[] second)
    {
        if (first.Length < 3 || second.Length < 2)
        {
            return false;
        }

        if (first.Any(point => IsPointInsidePolygon(point, second))
            || second.Any(point => IsPointInsidePolygon(point, first)))
        {
            return true;
        }

        for (var i = 0; i < first.Length; i++)
        {
            var firstStart = first[i];
            var firstEnd = first[(i + 1) % first.Length];
            for (var j = 0; j < second.Length; j++)
            {
                if (SegmentsIntersect(
                        firstStart,
                        firstEnd,
                        second[j],
                        second[(j + 1) % second.Length]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool XclipVisibleRegionIntersects(
        Extents3d window,
        Extents3d blockBounds,
        Point3d[] boundary,
        bool inverted)
    {
        if (!TryIntersect(window, blockBounds, out var overlap))
        {
            return false;
        }

        // 普通 XCLIP 保留边界内；反向 XCLIP 保留块范围中的边界外。
        return inverted
            ? !IsRectangleInsidePolygon(overlap, boundary)
            : IntersectsRectangleAndPolygon(overlap, boundary);
    }

    private static bool TryIntersect(Extents3d first, Extents3d second, out Extents3d overlap)
    {
        var minX = Math.Max(first.MinPoint.X, second.MinPoint.X);
        var minY = Math.Max(first.MinPoint.Y, second.MinPoint.Y);
        var maxX = Math.Min(first.MaxPoint.X, second.MaxPoint.X);
        var maxY = Math.Min(first.MaxPoint.Y, second.MaxPoint.Y);
        if (minX > maxX || minY > maxY)
        {
            overlap = default;
            return false;
        }

        overlap = new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
        return true;
    }

    private static bool IsRectangleInsidePolygon(Extents3d rectangle, Point3d[] polygon)
    {
        var corners = GetRectangleCorners(rectangle);
        if (!corners.All(point => IsPointInsidePolygon(point, polygon)))
        {
            return false;
        }

        for (var i = 0; i < polygon.Length; i++)
        {
            for (var edge = 0; edge < corners.Length; edge++)
            {
                if (SegmentsProperlyIntersect(
                        polygon[i],
                        polygon[(i + 1) % polygon.Length],
                        corners[edge],
                        corners[(edge + 1) % corners.Length]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static Extents3d GetBounds(IEnumerable<Point3d> points)
    {
        var items = points.ToArray();
        return new Extents3d(
            new Point3d(items.Min(point => point.X), items.Min(point => point.Y), 0),
            new Point3d(items.Max(point => point.X), items.Max(point => point.Y), 0));
    }

    private static Point3d[] GetRectangleCorners(Extents3d rectangle)
    {
        return new[]
        {
            new Point3d(rectangle.MinPoint.X, rectangle.MinPoint.Y, 0),
            new Point3d(rectangle.MinPoint.X, rectangle.MaxPoint.Y, 0),
            new Point3d(rectangle.MaxPoint.X, rectangle.MaxPoint.Y, 0),
            new Point3d(rectangle.MaxPoint.X, rectangle.MinPoint.Y, 0)
        };
    }

    private static bool IsPointInside(Extents3d rectangle, Point3d point)
    {
        return point.X >= rectangle.MinPoint.X
            && point.X <= rectangle.MaxPoint.X
            && point.Y >= rectangle.MinPoint.Y
            && point.Y <= rectangle.MaxPoint.Y;
    }

    private static bool IsPointInsidePolygon(Point3d point, Point3d[] polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Length - 1;
             current < polygon.Length;
             previous = current++)
        {
            var start = polygon[previous];
            var end = polygon[current];
            if (IsPointOnSegment(point, start, end))
            {
                return true;
            }

            var crossesScanLine = (start.Y > point.Y) != (end.Y > point.Y);
            if (crossesScanLine
                && point.X < (end.X - start.X) * (point.Y - start.Y) / (end.Y - start.Y) + start.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool SegmentsIntersect(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        if (IsPointOnSegment(a, c, d)
            || IsPointOnSegment(b, c, d)
            || IsPointOnSegment(c, a, b)
            || IsPointOnSegment(d, a, b))
        {
            return true;
        }

        return (Cross(a, b, c) > 0) != (Cross(a, b, d) > 0)
            && (Cross(c, d, a) > 0) != (Cross(c, d, b) > 0);
    }

    private static bool SegmentsProperlyIntersect(Point3d a, Point3d b, Point3d c, Point3d d)
    {
        return Cross(a, b, c) * Cross(a, b, d) < 0
            && Cross(c, d, a) * Cross(c, d, b) < 0;
    }

    private static bool IsPointOnSegment(Point3d point, Point3d start, Point3d end)
    {
        const double epsilon = 1e-9;
        var scale = 1 + Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y);
        if (Math.Abs(Cross(start, end, point)) > epsilon * scale)
        {
            return false;
        }

        return point.X >= Math.Min(start.X, end.X) - epsilon
            && point.X <= Math.Max(start.X, end.X) + epsilon
            && point.Y >= Math.Min(start.Y, end.Y) - epsilon
            && point.Y <= Math.Max(start.Y, end.Y) + epsilon;
    }

    private static double Cross(Point3d start, Point3d end, Point3d point)
    {
        return (end.X - start.X) * (point.Y - start.Y)
            - (end.Y - start.Y) * (point.X - start.X);
    }

    /// <summary>XCLIP 边界的实际世界点，供 UCS 下按扫描四点法变换，不先取 WCS 包盒。</summary>
    private static bool TryGetXclipBoundary(
        Transaction tr,
        Entity entity,
        out Point3d[] worldPoints,
        out bool inverted)
    {
        worldPoints = Array.Empty<Point3d>();
        inverted = false;
        if (entity is not BlockReference blockRef || blockRef.ExtensionDictionary.IsNull)
        {
            return false;
        }

        try
        {
            if (tr.GetObject(blockRef.ExtensionDictionary, OpenMode.ForRead, false) is not DBDictionary extDict
                || !extDict.Contains("ACAD_FILTER"))
            {
                return false;
            }

            if (tr.GetObject(extDict.GetAt("ACAD_FILTER"), OpenMode.ForRead, false) is not DBDictionary filterDict
                || !filterDict.Contains("SPATIAL"))
            {
                return false;
            }

            if (tr.GetObject(filterDict.GetAt("SPATIAL"), OpenMode.ForRead, false) is not SpatialFilter filter)
            {
                return false;
            }

            var definition = filter.Definition;
            if (!definition.Enabled)
            {
                return false;
            }

#if AUTOCAD && !ACAD_CORE
            // 2015 编译基线未暴露 Inverted；新版宿主运行时可通过反射读取。
            inverted = filter.GetType().GetProperty("Inverted")?.GetValue(filter) is true;
#else
            inverted = filter.Inverted;
#endif

            var points = definition.GetPoints();
            // SDK 明确定义该矩阵用于把裁剪边界坐标直接变到 WCS。
            var toWorld = filter.ClipSpaceToWorldCoordinateSystemTransform;
            if (points != null && points.Count >= 2)
            {
                var localPoints = new List<Point3d>();
                if (points.Count == 2)
                {
                    // 两点矩形必须先在裁剪坐标系补齐四角，再做旋转/镜像变换。
                    var minX = Math.Min(points[0].X, points[1].X);
                    var minY = Math.Min(points[0].Y, points[1].Y);
                    var maxX = Math.Max(points[0].X, points[1].X);
                    var maxY = Math.Max(points[0].Y, points[1].Y);
                    localPoints.Add(new Point3d(minX, minY, 0));
                    localPoints.Add(new Point3d(minX, maxY, 0));
                    localPoints.Add(new Point3d(maxX, maxY, 0));
                    localPoints.Add(new Point3d(maxX, minY, 0));
                }
                else
                {
                    for (var i = 0; i < points.Count; i++)
                    {
                        localPoints.Add(new Point3d(points[i].X, points[i].Y, 0));
                    }
                }

                worldPoints = localPoints.Select(point => point.TransformBy(toWorld)).ToArray();
                return true;
            }

            var queryBounds = filter.GetQueryBounds();
            worldPoints = new[]
            {
                new Point3d(queryBounds.MinPoint.X, queryBounds.MinPoint.Y, 0).TransformBy(toWorld),
                new Point3d(queryBounds.MinPoint.X, queryBounds.MaxPoint.Y, 0).TransformBy(toWorld),
                new Point3d(queryBounds.MaxPoint.X, queryBounds.MaxPoint.Y, 0).TransformBy(toWorld),
                new Point3d(queryBounds.MaxPoint.X, queryBounds.MinPoint.Y, 0).TransformBy(toWorld)
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Intersects(Extents3d a, Extents3d b)
    {
        return a.MinPoint.X <= b.MaxPoint.X
            && a.MaxPoint.X >= b.MinPoint.X
            && a.MinPoint.Y <= b.MaxPoint.Y
            && a.MaxPoint.Y >= b.MinPoint.Y;
    }

}
