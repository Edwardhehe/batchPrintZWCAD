using System;
using System.IO;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#elif ACAD_CORE
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public static class CadTextUpdater
{
    public static bool TryUpdateOpenDocument(PlotJob job, string? newTitle, string? newNumber, Document currentDocument, out string message)
    {
        message = "";
        var doc = FindTargetDocument(job, currentDocument);
        if (doc == null)
        {
            message = "对应 DWG 未打开，已只修改表格，未反写 CAD。";
            return false;
        }

        using (doc.LockDocument())
        {
            return TryUpdate(doc.Database, job, newTitle, newNumber, out message);
        }
    }

    private static bool TryUpdate(Database db, PlotJob job, string? newTitle, string? newNumber, out string message)
    {
        message = "";
        var library = TitleBlockLibraryStore.Load();
        var definition = library.Blocks.FirstOrDefault(x => string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            message = "图框库中找不到对应块定义。";
            return false;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var owner = FindOwnerRecord(tr, blockTable, job.SpaceName);
        if (owner == null)
        {
            message = "找不到对应空间。";
            return false;
        }

        var blockRef = FindBlockReference(tr, owner, job, definition);
        if (blockRef == null)
        {
            message = "找不到对应图框块。";
            return false;
        }

        var coordinateMode = GetCoordinateMode(definition);
        var referenceFrame = ResolveReferenceFrame(definition, blockRef);
        var titleRegion = ResolveLocalRegion(definition.TitleRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
        var numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);

        var changed = 0;
        if (newTitle != null)
        {
            changed += UpdateRegionText(tr, owner, blockRef, titleRegion, newTitle);
        }

        if (newNumber != null)
        {
            changed += UpdateRegionText(tr, owner, blockRef, numberRegion, newNumber);
        }

        if (changed == 0)
        {
            message = "未找到可写文字。若图名/图号是块定义里的固定文字，请改成属性或图框外独立文字。";
            return false;
        }

        tr.Commit();
        message = $"已反写 CAD 文字 {changed} 处。";
        return true;
    }

    private static BlockTableRecord? FindOwnerRecord(Transaction tr, BlockTable blockTable, string spaceName)
    {
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            if (string.Equals(owner.Name, spaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, spaceName, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    private static BlockReference? FindBlockReference(Transaction tr, BlockTableRecord owner, PlotJob job, TitleBlockDefinition definition)
    {
        var matches = owner
            .Cast<ObjectId>()
            .Select(id => tr.GetObject(id, OpenMode.ForRead, false))
            .OfType<BlockReference>()
            .Where(blockRef => string.Equals(CadTextExtractor.GetBlockName(blockRef, tr), job.BlockName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var coordinateMode = GetCoordinateMode(definition);
        var byHandle = matches.FirstOrDefault(blockRef =>
            !string.IsNullOrWhiteSpace(job.BlockHandle)
            && string.Equals(blockRef.Handle.ToString(), job.BlockHandle, StringComparison.OrdinalIgnoreCase));
        var byOriginalText = matches.FirstOrDefault(blockRef =>
        {
            var referenceFrame = ResolveReferenceFrame(definition, blockRef);
            var titleRegion = ResolveLocalRegion(definition.TitleRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
            var numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
            return string.Equals(CadTextExtractor.ExtractRegionText(tr, blockRef, owner, numberRegion), job.CadDrawingNumber, StringComparison.Ordinal)
                && string.Equals(CadTextExtractor.ExtractRegionText(tr, blockRef, owner, titleRegion), job.CadTitle, StringComparison.Ordinal);
        });

        return byHandle ?? byOriginalText ?? (matches.Count == 1 ? matches[0] : null);
    }

    private static int UpdateRegionText(Transaction tr, BlockTableRecord owner, BlockReference blockRef, LocalRectangle region, string value)
    {
        var changed = 0;
        var inverse = blockRef.BlockTransform.Inverse();

        foreach (ObjectId attributeId in blockRef.AttributeCollection)
        {
            if (!attributeId.IsValid || attributeId.IsErased)
            {
                continue;
            }

            if (tr.GetObject(attributeId, OpenMode.ForWrite, false) is AttributeReference attribute)
            {
                var local = attribute.Position.TransformBy(inverse);
                if (IsInRegion(attribute, inverse, region, local))
                {
                    attribute.TextString = value;
                    changed++;
                }
            }
        }

        foreach (ObjectId id in owner)
        {
            if (id == blockRef.ObjectId)
            {
                continue;
            }

            if (tr.GetObject(id, OpenMode.ForWrite, false) is not Entity entity)
            {
                continue;
            }

            if (entity is DBText dbText)
            {
                var local = dbText.Position.TransformBy(inverse);
                if (IsInRegion(dbText, inverse, region, local))
                {
                    dbText.TextString = value;
                    changed++;
                }
            }
            else if (entity is MText mText)
            {
                var local = mText.Location.TransformBy(inverse);
                if (IsInRegion(mText, inverse, region, local))
                {
                    mText.Contents = value;
                    changed++;
                }
            }
        }

        return changed;
    }

    private static bool IsInRegion(Entity entity, Matrix3d entityToLocal, LocalRectangle region, Point3d fallbackPoint)
    {
        if (TryGetTransformedExtents(entity, entityToLocal, out var extents))
        {
            return Intersects(region, extents);
        }

        return region.Contains(fallbackPoint.X, fallbackPoint.Y);
    }

    private static bool TryGetTransformedExtents(Entity entity, Matrix3d transform, out LocalRectangle rectangle)
    {
        rectangle = new LocalRectangle();
        try
        {
            var extents = entity.GeometricExtents;
            var points = new[]
            {
                new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
                new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
            };

            rectangle = LocalRectangle.FromPoints(
                points.Min(p => p.X),
                points.Min(p => p.Y),
                points.Max(p => p.X),
                points.Max(p => p.Y));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RegionCoordinateMode GetCoordinateMode(TitleBlockDefinition definition)
    {
        if (string.Equals(definition.CoordinateMode, "Frame", StringComparison.OrdinalIgnoreCase))
        {
            return RegionCoordinateMode.Frame;
        }

        return string.Equals(definition.CoordinateMode, "World", StringComparison.OrdinalIgnoreCase)
            ? RegionCoordinateMode.World
            : RegionCoordinateMode.Local;
    }

    private static LocalRectangle ResolveLocalRegion(LocalRectangle region, Matrix3d blockTransform, RegionCoordinateMode mode, LocalRectangle referenceFrame)
    {
        if (mode == RegionCoordinateMode.Frame)
        {
            return OffsetRegion(region, referenceFrame.MinX, referenceFrame.MinY);
        }

        if (mode == RegionCoordinateMode.Local)
        {
            return region;
        }

        var inverse = blockTransform.Inverse();
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(inverse),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(inverse),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(inverse),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(inverse)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle ResolveReferenceFrame(TitleBlockDefinition definition, BlockReference blockRef)
    {
        var blockFrame = TransformExtents(blockRef.GeometricExtents, blockRef.BlockTransform.Inverse());
        if (HasArea(definition.PrintRegion))
        {
            if (HasMeaningfulOverlap(definition.PrintRegion, blockFrame))
            {
                return definition.PrintRegion;
            }

            return blockFrame;
        }

        return blockFrame;
    }

    private static bool HasMeaningfulOverlap(LocalRectangle a, LocalRectangle b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return false;
        }

        var smallerArea = Math.Min(RectangleArea(a), RectangleArea(b));
        return smallerArea > 0 && overlapArea / smallerArea >= 0.25;
    }

    private static double RectangleArea(LocalRectangle rectangle)
    {
        return Math.Max(0, rectangle.MaxX - rectangle.MinX)
            * Math.Max(0, rectangle.MaxY - rectangle.MinY);
    }

    private static LocalRectangle TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle OffsetRegion(LocalRectangle region, double offsetX, double offsetY)
    {
        return LocalRectangle.FromPoints(
            region.MinX + offsetX,
            region.MinY + offsetY,
            region.MaxX + offsetX,
            region.MaxY + offsetY);
    }

    private static bool HasArea(LocalRectangle region)
    {
        return Math.Abs(region.MaxX - region.MinX) > 1e-6
            && Math.Abs(region.MaxY - region.MinY) > 1e-6;
    }

    private static bool Intersects(LocalRectangle a, LocalRectangle b)
    {
        return a.MinX <= b.MaxX
            && a.MaxX >= b.MinX
            && a.MinY <= b.MaxY
            && a.MaxY >= b.MinY;
    }

    private static Document? FindTargetDocument(PlotJob job, Document currentDocument)
    {
        if (IsSameDocument(job.SourceFile, currentDocument))
        {
            return currentDocument;
        }

        foreach (Document doc in CadApp.DocumentManager)
        {
            if (IsSameDocument(job.SourceFile, doc))
            {
                return doc;
            }
        }

        return null;
    }

    private static bool IsSameDocument(string sourceFile, Document doc)
    {
        var docFile = doc.Database.Filename;
        if (string.IsNullOrWhiteSpace(sourceFile) || string.IsNullOrWhiteSpace(docFile))
        {
            return string.Equals(sourceFile, doc.Name, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(docFile), StringComparison.OrdinalIgnoreCase);
    }

    private enum RegionCoordinateMode
    {
        Local,
        World,
        Frame
    }
}
