using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 矩形图框的纯几何识别与变换。
/// 调用方负责实体可见性、图层、XCLIP、缓存等业务策略，本类只判断几何是否构成矩形。
/// </summary>
internal static class RectangleGeometry
{
    /// <summary>从轻量多段线提取矩形。</summary>
    internal static bool TryGetRectangle(
        Polyline polyline,
        Matrix3d transform,
        bool requireClosed,
        out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        if (polyline.NumberOfVertices < 4)
        {
            return false;
        }

        // 图框边界只接受直线段；任一 bulge 非零都表示包含圆弧。
        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            if (Math.Abs(polyline.GetBulgeAt(index)) > 1e-9)
            {
                return false;
            }
        }

        var points = Enumerable.Range(0, polyline.NumberOfVertices)
            .Select(index => polyline.GetPoint3dAt(index).TransformBy(transform))
            .ToList();

        return TryBuildRectangle(points, polyline.Closed, requireClosed, out rectangle);
    }

    /// <summary>从老式二维多段线（POLYLINE+VERTEX）提取矩形。</summary>
    internal static bool TryGetRectangleFrom2d(
        Transaction tr,
        Polyline2d polyline,
        Matrix3d transform,
        bool requireClosed,
        out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        var points = new List<Point3d>();
        foreach (ObjectId vertexId in polyline)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead, false) is not Vertex2d vertex)
            {
                continue;
            }

            // 非零类型通常是样条拟合控制点，不属于实际折线顶点。
            if ((int)vertex.VertexType != 0)
            {
                continue;
            }

            if (Math.Abs(vertex.Bulge) > 1e-9)
            {
                return false;
            }

            points.Add(vertex.Position.TransformBy(transform));
        }

        return TryBuildRectangle(points, polyline.Closed, requireClosed, out rectangle);
    }

    /// <summary>从三维多段线（3DPOLY）提取投影到 XY 平面的矩形。</summary>
    internal static bool TryGetRectangleFrom3d(
        Transaction tr,
        Polyline3d polyline,
        Matrix3d transform,
        bool requireClosed,
        out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        var points = new List<Point3d>();
        foreach (ObjectId vertexId in polyline)
        {
            if (tr.GetObject(vertexId, OpenMode.ForRead, false) is not PolylineVertex3d vertex)
            {
                continue;
            }

            // 只读取线型顶点，排除样条曲线控制点；与既有矩形扫描兼容。
            if ((int)vertex.VertexType != 0)
            {
                continue;
            }

            var transformed = vertex.Position.TransformBy(transform);
            points.Add(new Point3d(transformed.X, transformed.Y, 0));
        }

        return TryBuildRectangle(points, polyline.Closed, requireClosed, out rectangle);
    }

    /// <summary>
    /// 将矩形四角变换到目标坐标系，并根据变换后的角点重算包围盒和实际边长。
    /// 块实例可能旋转或缩放，ActualWidth/ActualHeight 不能沿用定义空间尺寸。
    /// </summary>
    internal static LocalRectangle TransformRectangle(LocalRectangle rectangle, Matrix3d transform)
    {
        Point3d[] corners;
        if (rectangle.CornerPoints is { Length: >= 8 } cp)
        {
            corners = new[]
            {
                new Point3d(cp[0], cp[1], 0),
                new Point3d(cp[2], cp[3], 0),
                new Point3d(cp[4], cp[5], 0),
                new Point3d(cp[6], cp[7], 0)
            };
        }
        else
        {
            // 兼容老数据：无真实角点时从局部 Min/Max 补齐四角，避免旋转后只变换对角点造成包盒错误。
            corners = new[]
            {
                new Point3d(rectangle.MinX, rectangle.MinY, 0),
                new Point3d(rectangle.MaxX, rectangle.MinY, 0),
                new Point3d(rectangle.MaxX, rectangle.MaxY, 0),
                new Point3d(rectangle.MinX, rectangle.MaxY, 0)
            };
        }

        for (var index = 0; index < corners.Length; index++)
        {
            corners[index] = corners[index].TransformBy(transform);
        }

        var result = LocalRectangle.FromPoints(
            corners.Min(point => point.X),
            corners.Min(point => point.Y),
            corners.Max(point => point.X),
            corners.Max(point => point.Y));

        var side01 = corners[0].DistanceTo(corners[1]);
        var side12 = corners[1].DistanceTo(corners[2]);
        result.ActualWidth = Math.Max(side01, side12);
        result.ActualHeight = Math.Min(side01, side12);
        result.CornerPoints = new[]
        {
            corners[0].X, corners[0].Y,
            corners[1].X, corners[1].Y,
            corners[2].X, corners[2].Y,
            corners[3].X, corners[3].Y
        };
        return result;
    }

    /// <summary>取矩形实际面积，优先使用真实边长，退化数据才使用轴对齐包围盒。</summary>
    internal static double GetActualArea(LocalRectangle rectangle)
    {
        var width = rectangle.ActualWidth > 0
            ? rectangle.ActualWidth
            : Math.Max(0, rectangle.MaxX - rectangle.MinX);
        var height = rectangle.ActualHeight > 0
            ? rectangle.ActualHeight
            : Math.Max(0, rectangle.MaxY - rectangle.MinY);
        return width * height;
    }

    private static bool TryBuildRectangle(
        List<Point3d> points,
        bool entityClosed,
        bool requireClosed,
        out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        if (points.Count < 4)
        {
            return false;
        }

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

        // 沿用既有扫描容差：包围盒长边的 0.1%。
        var tolerance = Math.Max(boxWidth, boxHeight) * 0.001;
        var endpointsMeet = points.Count > 1 && SamePoint(points[0], points[points.Count - 1], tolerance);
        if (requireClosed && !entityClosed && !endpointsMeet)
        {
            return false;
        }

        if (endpointsMeet)
        {
            points.RemoveAt(points.Count - 1);
        }

        RemoveConsecutiveDuplicatePoints(points, tolerance);
        RemoveCollinearPoints(points, tolerance);
        if (points.Count != 4)
        {
            return false;
        }

        // 对角线等长且互相平分，可验证任意方向的矩形，不依赖WCS/UCS轴向。
        var diagonal02 = points[0].DistanceTo(points[2]);
        var diagonal13 = points[1].DistanceTo(points[3]);
        if (Math.Abs(diagonal02 - diagonal13) > tolerance)
        {
            return false;
        }

        var midpoint02 = new Point3d(
            (points[0].X + points[2].X) / 2d,
            (points[0].Y + points[2].Y) / 2d,
            0);
        var midpoint13 = new Point3d(
            (points[1].X + points[3].X) / 2d,
            (points[1].Y + points[3].Y) / 2d,
            0);
        if (midpoint02.DistanceTo(midpoint13) > tolerance)
        {
            return false;
        }

        var side01 = points[0].DistanceTo(points[1]);
        var side12 = points[1].DistanceTo(points[2]);
        rectangle = LocalRectangle.FromPoints(minX, minY, maxX, maxY);
        rectangle.ActualWidth = Math.Max(side01, side12);
        rectangle.ActualHeight = Math.Min(side01, side12);
        rectangle.CornerPoints = new[]
        {
            points[0].X, points[0].Y,
            points[1].X, points[1].Y,
            points[2].X, points[2].Y,
            points[3].X, points[3].Y
        };
        return true;
    }

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
                var v1x = current.X - previous.X;
                var v1y = current.Y - previous.Y;
                var v2x = next.X - current.X;
                var v2y = next.Y - current.Y;
                var crossZ = v1x * v2y - v1y * v2x;
                if (Math.Abs(crossZ) > tolerance)
                {
                    continue;
                }

                points.RemoveAt(index);
                changed = true;
                break;
            }
        }
    }

    private static bool SamePoint(Point3d a, Point3d b, double tolerance)
    {
        return Math.Abs(a.X - b.X) <= tolerance
            && Math.Abs(a.Y - b.Y) <= tolerance;
    }
}
