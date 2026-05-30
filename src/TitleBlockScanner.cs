using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;

namespace ZwcadBatchPlot;

public static class TitleBlockScanner
{
    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(doc.Database, library, sourceName);
    }

    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library, Extents3d? scanWindow)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(doc.Database, library, sourceName, scanWindow);
    }

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName)
    {
        return Scan(db, library, sourceName, null);
    }

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName, Extents3d? scanWindow)
    {
        var jobs = new List<PlotJob>();
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = db.Filename;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        var matchIndex = 0;
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            foreach (ObjectId id in owner)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                {
                    continue;
                }

                var blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                var definition = library.Blocks.FirstOrDefault(x =>
                    string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
                if (definition == null)
                {
                    continue;
                }

                Extents3d extents;
                LocalRectangle titleRegion;
                LocalRectangle numberRegion;
                RegionCoordinateMode coordinateMode;
                try
                {
                    coordinateMode = GetCoordinateMode(definition);
                    extents = definition.HasPrintRegion
                        ? ResolveWorldExtents(definition.PrintRegion, blockRef.BlockTransform, coordinateMode)
                        : blockRef.GeometricExtents;
                    titleRegion = ResolveLocalRegion(definition.TitleRegion, blockRef.BlockTransform, coordinateMode);
                    numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, blockRef.BlockTransform, coordinateMode);
                }
                catch
                {
                    continue;
                }

                if (scanWindow.HasValue && !Intersects(extents, scanWindow.Value))
                {
                    continue;
                }

                var width = extents.MaxPoint.X - extents.MinPoint.X;
                var height = extents.MaxPoint.Y - extents.MinPoint.Y;
                var detectedPaper = PaperSizeDetector.Detect(width, height);
                var paper = ApplyFixedPaper(definition, detectedPaper);
                var title = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, titleRegion);
                var number = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, numberRegion);

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "未识别图名";
                }

                if (string.IsNullOrWhiteSpace(number))
                {
                    number = "未识别图号";
                }

                var boundaryNote = definition.HasPrintRegion ? "打印边界: 图框库框选边界" : "打印边界: 块外包框";
                if (coordinateMode == RegionCoordinateMode.World)
                {
                    boundaryNote += "；图框库坐标模式: 图纸坐标";
                }
                else
                {
                    boundaryNote += "；图框库坐标模式: 块内坐标";
                }

                jobs.Add(new PlotJob
                {
                    SourceFile = sourceName,
                    SpaceName = owner.Name,
                    IsPaperSpace = !string.Equals(owner.Name, BlockTableRecord.ModelSpace, StringComparison.OrdinalIgnoreCase),
                    BlockName = blockName,
                    MatchIndex = matchIndex++,
                    DrawingNumber = number,
                    Title = title,
                    CadDrawingNumber = number,
                    CadTitle = title,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{Math.Abs(width):0.##} x {Math.Abs(height):0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = $"{boundaryNote}; {paper.Note}",
                    PaperWidthMm = paper.PaperWidthMm,
                    PaperHeightMm = paper.PaperHeightMm,
                    MinX = extents.MinPoint.X,
                    MinY = extents.MinPoint.Y,
                    MaxX = extents.MaxPoint.X,
                    MaxY = extents.MaxPoint.Y
                });
            }
        }

        tr.Commit();
        return jobs
            .OrderBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => Path.GetFileName(x.SourceFile), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RegionCoordinateMode GetCoordinateMode(TitleBlockDefinition definition)
    {
        return string.Equals(definition.CoordinateMode, "World", StringComparison.OrdinalIgnoreCase)
            ? RegionCoordinateMode.World
            : RegionCoordinateMode.Local;
    }

    private static Extents3d ResolveWorldExtents(LocalRectangle region, Matrix3d blockTransform, RegionCoordinateMode mode)
    {
        return mode == RegionCoordinateMode.World
            ? ToExtents(region)
            : TransformRegion(region, blockTransform);
    }

    private static LocalRectangle ResolveLocalRegion(LocalRectangle region, Matrix3d blockTransform, RegionCoordinateMode mode)
    {
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

    private static bool Intersects(Extents3d a, Extents3d b)
    {
        return a.MinPoint.X <= b.MaxPoint.X
            && a.MaxPoint.X >= b.MinPoint.X
            && a.MinPoint.Y <= b.MaxPoint.Y
            && a.MaxPoint.Y >= b.MinPoint.Y;
    }

    private static Extents3d ToExtents(LocalRectangle region)
    {
        return new Extents3d(new Point3d(region.MinX, region.MinY, 0), new Point3d(region.MaxX, region.MaxY, 0));
    }

    private static Extents3d TransformRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
        };

        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
    }

    private static PaperDetection ApplyFixedPaper(TitleBlockDefinition definition, PaperDetection detected)
    {
        if (definition.PaperWidthMm <= 0 || definition.PaperHeightMm <= 0)
        {
            return detected;
        }

        var name = string.IsNullOrWhiteSpace(definition.PaperName)
            ? detected.PaperName
            : definition.PaperName;

        return new PaperDetection
        {
            PaperName = name,
            ScaleText = detected.ScaleText,
            ScaleValue = detected.ScaleValue,
            IsLong = name.EndsWith("+", StringComparison.OrdinalIgnoreCase),
            PaperWidthMm = definition.PaperWidthMm,
            PaperHeightMm = definition.PaperHeightMm,
            Note = $"固定纸张来自图框库，输出纸张 {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm；比例按图框边界自动识别为 {detected.ScaleText}"
        };
    }

    private enum RegionCoordinateMode
    {
        Local,
        World
    }
}
