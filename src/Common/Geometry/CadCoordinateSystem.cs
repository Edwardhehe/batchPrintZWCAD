using System;
using System.Collections.Generic;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 统一管理框选、扫描和打印之间的 UCS/WCS 坐标上下文。
/// 旋转 UCS 下必须保留“UCS 矩形 + UCS 基轴”，不能只留下它的 WCS 包围盒，
/// 否则后续再转换到 DCS 时会发生二次包围，打印范围被放大。
/// </summary>
public static class CadCoordinateSystem
{
    public static CadSelectionWindow CreateSelectionWindow(
        Editor editor,
        Point3d firstUcs,
        Point3d secondUcs,
        bool useUserCoordinateSystem)
    {
        var ucsToWorld = useUserCoordinateSystem ? GetUcsToWorld(editor) : Matrix3d.Identity;
        return new CadSelectionWindow
        {
            Bounds = LocalRectangle.FromPoints(firstUcs.X, firstUcs.Y, secondUcs.X, secondUcs.Y),
            UcsToWorld = ucsToWorld,
            WorldToUcs = ucsToWorld.Inverse()
        };
    }

    /// <summary>模型空间沿用当前 UCS；布局空间始终按 WCS/纸空间坐标处理。</summary>
    public static CadSelectionWindow CreateModelContext(Editor editor, bool isModelSpace)
    {
        var ucsToWorld = isModelSpace ? GetUcsToWorld(editor) : Matrix3d.Identity;
        return new CadSelectionWindow
        {
            UcsToWorld = ucsToWorld,
            WorldToUcs = ucsToWorld.Inverse()
        };
    }

    private static Matrix3d GetUcsToWorld(Editor editor)
    {
        try
        {
            return editor.CurrentUserCoordinateSystem;
        }
        catch
        {
            return Matrix3d.Identity;
        }
    }
}

/// <summary>一次扫描使用的坐标上下文；Bounds 位于当前 UCS，实体仍以 WCS 写入数据库。</summary>
public sealed class CadSelectionWindow
{
    public LocalRectangle Bounds { get; set; } = new();
    public Matrix3d UcsToWorld { get; set; } = Matrix3d.Identity;
    public Matrix3d WorldToUcs { get; set; } = Matrix3d.Identity;

    public bool IsWorldCoordinateSystem
    {
        get
        {
            var coordinateSystem = UcsToWorld.CoordinateSystem3d;
            return coordinateSystem.Origin.DistanceTo(Point3d.Origin) <= 1e-8
                && (coordinateSystem.Xaxis - Vector3d.XAxis).Length <= 1e-8
                && (coordinateSystem.Yaxis - Vector3d.YAxis).Length <= 1e-8;
        }
    }

    public LocalRectangle TransformWorldPointsToBounds(IEnumerable<Point3d> worldPoints)
    {
        var points = worldPoints.Select(point => point.TransformBy(WorldToUcs)).ToArray();
        return LocalRectangle.FromPoints(
            points.Min(point => point.X),
            points.Min(point => point.Y),
            points.Max(point => point.X),
            points.Max(point => point.Y));
    }

    public LocalRectangle TransformWorldPointsToBounds(double[] worldPoints)
    {
        if (worldPoints.Length < 8)
        {
            throw new ArgumentException("矩形角点数组至少需要 8 个坐标值。", nameof(worldPoints));
        }

        return TransformWorldPointsToBounds(new[]
        {
            new Point3d(worldPoints[0], worldPoints[1], 0),
            new Point3d(worldPoints[2], worldPoints[3], 0),
            new Point3d(worldPoints[4], worldPoints[5], 0),
            new Point3d(worldPoints[6], worldPoints[7], 0)
        });
    }

    public bool IntersectsWorldPoints(IEnumerable<Point3d> worldPoints)
    {
        var entityBounds = TransformWorldPointsToBounds(worldPoints);
        return entityBounds.MaxX >= Bounds.MinX
            && entityBounds.MinX <= Bounds.MaxX
            && entityBounds.MaxY >= Bounds.MinY
            && entityBounds.MinY <= Bounds.MaxY;
    }

    /// <summary>
    /// 把当前 UCS 边界和基轴写入任务。Min/Max 继续保留 WCS 包围盒，供数据库相交、拆图等流程使用。
    /// </summary>
    public void ApplyToJob(PlotJob job, LocalRectangle ucsBounds)
    {
        if (job.IsPaperSpace || IsWorldCoordinateSystem)
        {
            return;
        }

        var coordinateSystem = UcsToWorld.CoordinateSystem3d;
        job.UsesUserCoordinateSystem = true;
        job.UcsMinX = ucsBounds.MinX;
        job.UcsMinY = ucsBounds.MinY;
        job.UcsMaxX = ucsBounds.MaxX;
        job.UcsMaxY = ucsBounds.MaxY;
        job.UcsOriginX = coordinateSystem.Origin.X;
        job.UcsOriginY = coordinateSystem.Origin.Y;
        job.UcsOriginZ = coordinateSystem.Origin.Z;
        job.UcsXAxisX = coordinateSystem.Xaxis.X;
        job.UcsXAxisY = coordinateSystem.Xaxis.Y;
        job.UcsXAxisZ = coordinateSystem.Xaxis.Z;
        job.UcsYAxisX = coordinateSystem.Yaxis.X;
        job.UcsYAxisY = coordinateSystem.Yaxis.Y;
        job.UcsYAxisZ = coordinateSystem.Yaxis.Z;
    }

    public static Matrix3d GetJobUcsToWorld(PlotJob job)
    {
        var origin = new Point3d(job.UcsOriginX, job.UcsOriginY, job.UcsOriginZ);
        var xAxis = new Vector3d(job.UcsXAxisX, job.UcsXAxisY, job.UcsXAxisZ).GetNormal();
        var yAxis = new Vector3d(job.UcsYAxisX, job.UcsYAxisY, job.UcsYAxisZ).GetNormal();
        var zAxis = xAxis.CrossProduct(yAxis).GetNormal();
        return Matrix3d.AlignCoordinateSystem(
            Point3d.Origin,
            Vector3d.XAxis,
            Vector3d.YAxis,
            Vector3d.ZAxis,
            origin,
            xAxis,
            yAxis,
            zAxis);
    }

    public static Point3d[] GetJobWorldCorners(PlotJob job)
    {
        if (!job.UsesUserCoordinateSystem)
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

            return GetCorners(LocalRectangle.FromPoints(job.MinX, job.MinY, job.MaxX, job.MaxY));
        }

        var transform = GetJobUcsToWorld(job);
        return GetCorners(LocalRectangle.FromPoints(job.UcsMinX, job.UcsMinY, job.UcsMaxX, job.UcsMaxY))
            .Select(point => point.TransformBy(transform))
            .ToArray();
    }

    public static Point3d[] GetCorners(LocalRectangle rectangle)
    {
        return new[]
        {
            new Point3d(rectangle.MinX, rectangle.MinY, 0),
            new Point3d(rectangle.MinX, rectangle.MaxY, 0),
            new Point3d(rectangle.MaxX, rectangle.MinY, 0),
            new Point3d(rectangle.MaxX, rectangle.MaxY, 0)
        };
    }
}
