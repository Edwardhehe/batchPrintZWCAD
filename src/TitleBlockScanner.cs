using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;

namespace ZwcadBatchPlot;

public static class TitleBlockScanner
{
    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library)
    {
        var jobs = new List<PlotJob>();
        var db = doc.Database;

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

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
                    extents = blockRef.GeometricExtents;
                }
                catch
                {
                    continue;
                }

                var width = extents.MaxPoint.X - extents.MinPoint.X;
                var height = extents.MaxPoint.Y - extents.MinPoint.Y;
                var paper = PaperSizeDetector.Detect(width, height);
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
                    SourceFile = string.IsNullOrWhiteSpace(db.Filename) ? doc.Name : db.Filename,
                    SpaceName = owner.Name,
                    BlockName = blockName,
                    DrawingNumber = number,
                    Title = title,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{Math.Abs(width):0.##} x {Math.Abs(height):0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = paper.Note,
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
}
