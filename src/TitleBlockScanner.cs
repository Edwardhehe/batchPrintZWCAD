using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
                    var referenceFrame = ResolveReferenceFrame(definition, blockRef);
                    extents = ResolveWorldExtents(definition, blockRef, coordinateMode, referenceFrame);
                    titleRegion = ResolveLocalRegion(definition.TitleRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
                    numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, blockRef.BlockTransform, coordinateMode, referenceFrame);
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
        return DeduplicateOverlappingJobs(jobs)
            .OrderBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => Path.GetFileName(x.SourceFile), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<PlotJob> DeduplicateOverlappingJobs(List<PlotJob> jobs)
    {
        var result = new List<PlotJob>();
        foreach (var job in jobs.OrderByDescending(ScoreJob))
        {
            var duplicateIndex = result.FindIndex(existing => GetOverlapRatio(existing, job) >= 0.9);
            if (duplicateIndex < 0)
            {
                result.Add(job);
                continue;
            }

            if (ScoreJob(job) > ScoreJob(result[duplicateIndex]))
            {
                result[duplicateIndex] = job;
            }
        }

        return result;
    }

    private static int ScoreJob(PlotJob job)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(job.Title) && !job.Title.Contains("未识别"))
        {
            score += 10;
        }

        if (!string.IsNullOrWhiteSpace(job.DrawingNumber) && !job.DrawingNumber.Contains("未识别"))
        {
            score += 10;
        }

        if (Regex.IsMatch(job.DrawingNumber ?? "", @"[A-Za-z].*\d"))
        {
            score += 10;
        }

        var combined = (job.Title ?? "") + " " + (job.DrawingNumber ?? "");
        if (Regex.IsMatch(combined, "审\\s*定|审\\s*核|校\\s*对|设\\s*计|项目负责人|专业负责人"))
        {
            score -= 20;
        }

        return score;
    }

    private static double GetOverlapRatio(PlotJob a, PlotJob b)
    {
        var overlapWidth = Math.Max(0, Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX));
        var overlapHeight = Math.Max(0, Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY));
        var overlapArea = overlapWidth * overlapHeight;
        if (overlapArea <= 0)
        {
            return 0;
        }

        var smallerArea = Math.Min(JobArea(a), JobArea(b));
        return smallerArea <= 0 ? 0 : overlapArea / smallerArea;
    }

    private static double JobArea(PlotJob job)
    {
        return Math.Max(0, job.MaxX - job.MinX) * Math.Max(0, job.MaxY - job.MinY);
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

    private static Extents3d ResolveWorldExtents(TitleBlockDefinition definition, BlockReference blockRef, RegionCoordinateMode mode, LocalRectangle referenceFrame)
    {
        if (mode == RegionCoordinateMode.World && definition.HasPrintRegion)
        {
            return ToExtents(definition.PrintRegion);
        }

        if (mode == RegionCoordinateMode.Frame)
        {
            return TransformRegion(referenceFrame, blockRef.BlockTransform);
        }

        return definition.HasPrintRegion
            ? TransformRegion(definition.PrintRegion, blockRef.BlockTransform)
            : blockRef.GeometricExtents;
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
        World,
        Frame
    }
}
