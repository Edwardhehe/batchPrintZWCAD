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

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName)
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
                try
                {
                    extents = definition.HasPrintRegion
                        ? TransformRegion(definition.PrintRegion, blockRef.BlockTransform)
                        : blockRef.GeometricExtents;
                }
                catch
                {
                    continue;
                }

                var width = extents.MaxPoint.X - extents.MinPoint.X;
                var height = extents.MaxPoint.Y - extents.MinPoint.Y;
                var paper = PaperSizeDetector.Detect(width, height);
                var boundaryNote = definition.HasPrintRegion ? "打印边界: 图框库框选边界" : "打印边界: 块外包框";
                var title = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, definition.TitleRegion);
                var number = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, definition.DrawingNumberRegion);

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = "未识别图名";
                }

                if (string.IsNullOrWhiteSpace(number))
                {
                    number = "未识别图号";
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
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{Math.Abs(width):0.##} x {Math.Abs(height):0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = $"{boundaryNote}；{paper.Note}",
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
}
