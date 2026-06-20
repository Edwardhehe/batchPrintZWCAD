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

public static class RectangleFrameScanner
{
    private const string TemporaryOverlayLayer = "ZBP_TEMP_SEQUENCE_OVERLAY";

    public sealed class Result
    {
        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> PaperOptions { get; set; } = Array.Empty<PaperDetection>();
    }

    public static List<Result> ScanWindow(Document document, Extents3d scanWindow)
    {
        var sourceFile = string.IsNullOrWhiteSpace(document.Database.Filename)
            ? document.Name
            : document.Database.Filename;
        var rectangles = new List<LocalRectangle>();

        using var tr = document.Database.TransactionManager.StartTransaction();
        var owner = (BlockTableRecord)tr.GetObject(document.Database.CurrentSpaceId, OpenMode.ForRead);
        if (!owner.IsLayout || owner.LayoutId.IsNull)
        {
            return new List<Result>();
        }

        var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
        var layoutName = layout.LayoutName;
        foreach (ObjectId id in owner)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity)
            {
                continue;
            }

            CollectEntityRectangles(tr, entity, Matrix3d.Identity, rectangles, new HashSet<ObjectId>(), 0);
        }

        tr.Commit();
        var window = LocalRectangle.FromPoints(
            scanWindow.MinPoint.X,
            scanWindow.MinPoint.Y,
            scanWindow.MaxPoint.X,
            scanWindow.MaxPoint.Y);
        var filtered = FilterRectangles(rectangles)
            .Where(rectangle => Intersects(rectangle, window))
            .ToList();
        var stem = Path.GetFileNameWithoutExtension(sourceFile);
        return filtered.Select((rectangle, index) =>
        {
            var width = rectangle.MaxX - rectangle.MinX;
            var height = rectangle.MaxY - rectangle.MinY;
            var options = PaperSizeDetector.DetectCandidates(width, height);
            var paper = options.First();
            return new Result
            {
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
            };
        }).ToList();
    }

    private static void CollectEntityRectangles(
        Transaction tr,
        Entity entity,
        Matrix3d transform,
        ICollection<LocalRectangle> rectangles,
        ISet<ObjectId> visitedDefinitions,
        int depth)
    {
        if (string.Equals(entity.Layer, TemporaryOverlayLayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (entity is Polyline polyline
            && IsEntityLayerScannable(tr, entity)
            && TryGetRectangle(polyline, transform, out var rectangle))
        {
            if (PaperSizeDetector.DetectCandidates(
                    rectangle.MaxX - rectangle.MinX,
                    rectangle.MaxY - rectangle.MinY).Count > 0)
            {
                rectangles.Add(rectangle);
            }
        }

        if (entity is not BlockReference blockReference || depth >= 12)
        {
            return;
        }

        var definitionId = blockReference.BlockTableRecord;
        if (!visitedDefinitions.Add(definitionId))
        {
            return;
        }

        try
        {
            var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
            var nestedTransform = blockReference.BlockTransform * transform;
            foreach (ObjectId id in definition)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is Entity nested)
                {
                    CollectEntityRectangles(tr, nested, nestedTransform, rectangles, visitedDefinitions, depth + 1);
                }
            }
        }
        catch
        {
        }
        finally
        {
            visitedDefinitions.Remove(definitionId);
        }
    }

    private static bool IsEntityLayerScannable(Transaction tr, Entity entity)
    {
        try
        {
            if (entity.LayerId.IsNull
                || tr.GetObject(entity.LayerId, OpenMode.ForRead, false) is not LayerTableRecord layer)
            {
                return false;
            }

            return !layer.IsOff
                && !layer.IsFrozen
                && layer.IsPlottable;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetRectangle(Polyline polyline, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        if (!polyline.Closed || polyline.NumberOfVertices != 4)
        {
            return false;
        }

        var points = Enumerable.Range(0, 4)
            .Select(index =>
            {
                var point = polyline.GetPoint3dAt(index);
                return point.TransformBy(transform);
            })
            .ToArray();
        var minX = points.Min(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxX = points.Max(point => point.X);
        var maxY = points.Max(point => point.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= 1e-6 || height <= 1e-6)
        {
            return false;
        }

        var tolerance = Math.Max(width, height) * 0.001;
        foreach (var point in points)
        {
            var onVertical = Math.Abs(point.X - minX) <= tolerance || Math.Abs(point.X - maxX) <= tolerance;
            var onHorizontal = Math.Abs(point.Y - minY) <= tolerance || Math.Abs(point.Y - maxY) <= tolerance;
            if (!onVertical || !onHorizontal)
            {
                return false;
            }
        }

        rectangle = LocalRectangle.FromPoints(minX, minY, maxX, maxY);
        return true;
    }

    private static List<LocalRectangle> FilterRectangles(IEnumerable<LocalRectangle> source)
    {
        var unique = new List<LocalRectangle>();
        foreach (var rectangle in source.OrderByDescending(Area))
        {
            var tolerance = Math.Max(rectangle.MaxX - rectangle.MinX, rectangle.MaxY - rectangle.MinY) * 0.002;
            if (unique.Any(existing => SameBounds(existing, rectangle, tolerance)))
            {
                continue;
            }

            unique.Add(rectangle);
        }

        return unique
            .Where(candidate => !unique.Any(container =>
                !ReferenceEquals(container, candidate)
                && Area(container) >= Area(candidate) * 1.5
                && Contains(container, candidate)))
            .ToList();
    }

    private static bool SameBounds(LocalRectangle a, LocalRectangle b, double tolerance)
    {
        return Math.Abs(a.MinX - b.MinX) <= tolerance
            && Math.Abs(a.MinY - b.MinY) <= tolerance
            && Math.Abs(a.MaxX - b.MaxX) <= tolerance
            && Math.Abs(a.MaxY - b.MaxY) <= tolerance;
    }

    private static bool Contains(LocalRectangle outer, LocalRectangle inner)
    {
        var tolerance = Math.Max(outer.MaxX - outer.MinX, outer.MaxY - outer.MinY) * 0.003;
        return inner.MinX >= outer.MinX - tolerance
            && inner.MinY >= outer.MinY - tolerance
            && inner.MaxX <= outer.MaxX + tolerance
            && inner.MaxY <= outer.MaxY + tolerance;
    }

    private static double Area(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    private static bool Intersects(LocalRectangle rectangle, LocalRectangle window)
    {
        return rectangle.MaxX >= window.MinX
            && rectangle.MinX <= window.MaxX
            && rectangle.MaxY >= window.MinY
            && rectangle.MinY <= window.MaxY;
    }
}
