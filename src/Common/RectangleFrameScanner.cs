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
            // Always scan the paper-space layout record itself. CurrentSpaceId can
            // point to model space while the user is editing through a viewport.
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
        if (polyline.NumberOfVertices < 4)
        {
            return false;
        }

        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            if (Math.Abs(polyline.GetBulgeAt(index)) > 1e-9)
            {
                return false;
            }
        }

        var points = Enumerable.Range(0, polyline.NumberOfVertices)
            .Select(index =>
            {
                var point = polyline.GetPoint3dAt(index);
                return point.TransformBy(transform);
            })
            .ToList();
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
        if (!polyline.Closed && !SamePoint(points[0], points[points.Count - 1], tolerance))
        {
            return false;
        }

        if (points.Count > 1 && SamePoint(points[0], points[points.Count - 1], tolerance))
        {
            points.RemoveAt(points.Count - 1);
        }

        RemoveConsecutiveDuplicatePoints(points, tolerance);
        RemoveCollinearPoints(points, tolerance);
        if (points.Count != 4)
        {
            return false;
        }

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
                var sameX = Math.Abs(previous.X - current.X) <= tolerance
                    && Math.Abs(current.X - next.X) <= tolerance;
                var sameY = Math.Abs(previous.Y - current.Y) <= tolerance
                    && Math.Abs(current.Y - next.Y) <= tolerance;
                if (!sameX && !sameY)
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

    private static List<LocalRectangle> FilterRectangles(IEnumerable<LocalRectangle> source)
    {
        var unique = new List<LocalRectangle>();
        foreach (var rectangle in source.OrderByDescending(Area))
        {
            var tolerance = Math.Max(rectangle.MaxX - rectangle.MinX, rectangle.MaxY - rectangle.MinY) * 0.002;
            if (unique.Any(existing =>
                    SameBounds(existing, rectangle, tolerance)
                    || HasDuplicateOverlap(existing, rectangle)))
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

        var smallerCoverage = overlapArea / Math.Min(areaA, areaB);
        var largerCoverage = overlapArea / Math.Max(areaA, areaB);
        var widthSimilarity = Math.Min(a.MaxX - a.MinX, b.MaxX - b.MinX)
            / Math.Max(a.MaxX - a.MinX, b.MaxX - b.MinX);
        var heightSimilarity = Math.Min(a.MaxY - a.MinY, b.MaxY - b.MinY)
            / Math.Max(a.MaxY - a.MinY, b.MaxY - b.MinY);

        // Treat nearly coincident frames as one drawing, while preserving
        // genuinely nested or adjacent rectangles with different dimensions.
        return smallerCoverage >= 0.90
            && largerCoverage >= 0.82
            && widthSimilarity >= 0.90
            && heightSimilarity >= 0.90;
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
