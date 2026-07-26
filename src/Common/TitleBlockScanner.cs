using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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

public static class TitleBlockScanner
{
    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(doc.Database, library, sourceName, null, TitleBlockScanScope.AllSpaces, GetCurrentSpaceName(doc.Database));
    }

    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library, TitleBlockScanScope scope, double? paperMatchToleranceMm = null)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(doc.Database, library, sourceName, null, scope, GetCurrentSpaceName(doc.Database), paperMatchToleranceMm);
    }

    public static List<PlotJob> Scan(Document doc, TitleBlockLibrary library, Extents3d? scanWindow, double? paperMatchToleranceMm = null)
    {
        var sourceName = string.IsNullOrWhiteSpace(doc.Database.Filename)
            ? doc.Name
            : doc.Database.Filename;
        return Scan(doc.Database, library, sourceName, scanWindow, TitleBlockScanScope.CurrentSpace, GetCurrentSpaceName(doc.Database), paperMatchToleranceMm);
    }

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName)
    {
        return Scan(db, library, sourceName, null);
    }

    public static List<PlotJob> Scan(Database db, TitleBlockLibrary library, string sourceName, Extents3d? scanWindow)
    {
        return Scan(db, library, sourceName, scanWindow, TitleBlockScanScope.AllSpaces, null);
    }

    public static List<PlotJob> Scan(
        Database db,
        TitleBlockLibrary library,
        string sourceName,
        Extents3d? scanWindow,
        TitleBlockScanScope scope,
        string? currentSpaceName = null,
        double? paperMatchToleranceMm = null)
    {
        var jobs = new List<PlotJob>();
        var warnings = new List<string>();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? AppSettingsStore.Load().PaperMatchToleranceMm;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = db.Filename;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        var libraryBlockNames = new HashSet<string>(
            library.Blocks.Select(x => x.BlockName).Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.OrdinalIgnoreCase);

        var matchIndex = 0;
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            var spaceName = layout.LayoutName;
            if (!ShouldScanLayout(layout, owner, scope, currentSpaceName))
            {
                continue;
            }

            CadTextExtractor.OwnerTextCache? ownerTextCache = null;
            try
            {
                ownerTextCache = CadTextExtractor.BuildOwnerTextCache(tr, owner, libraryBlockNames);
            }
            catch (Exception ex)
            {
                warnings.Add($"布局={spaceName} 文字缓存建立失败，将继续逐个扫描图框: {ex.Message}");
            }

            foreach (ObjectId id in owner)
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                {
                    continue;
                }

                string blockName;
                try
                {
                    blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                }
                catch (Exception ex)
                {
                    warnings.Add($"布局={spaceName} 句柄={blockRef.Handle} 块名读取失败: {ex.Message}");
                    continue;
                }

                var definition = library.Blocks.FirstOrDefault(x =>
                    string.Equals(x.BlockName, blockName, StringComparison.OrdinalIgnoreCase));

                // If no direct match, peek deeper: the outer block may be a
                // dynamic-block container whose visible inner block was registered instead.
                Matrix3d effectiveBlockTransform = blockRef.BlockTransform;
                string effectiveBlockName = blockName;
                // 嵌套匹配时需记录从内层块定义到外层块定义空间的累积变换，用于后续 region 坐标对齐。
                Matrix3d nestedToOuter = Matrix3d.Identity;
                bool isNestedMatch = false;
                if (definition == null)
                {
                    definition = ResolveNestedLibraryMatch(
                        tr, blockRef, library, out var nestedTransform);
                    if (definition != null)
                    {
                        effectiveBlockTransform = nestedTransform * blockRef.BlockTransform;
                        effectiveBlockName = definition.BlockName;
                        nestedToOuter = nestedTransform;
                        isNestedMatch = true;
                    }
                }

                if (definition == null)
                {
                    continue;
                }

                Extents3d extents;
                LocalRectangle titleRegion;
                LocalRectangle numberRegion;
                LocalRectangle dateRegion = new();
                LocalRectangle revisionRegion = new();
                LocalRectangle phaseRegion = new();
                LocalRectangle info1Region = new();
                LocalRectangle info2Region = new();
                RegionCoordinateMode coordinateMode;
                LocalRectangle referenceFrame;
                try
                {
                    coordinateMode = GetCoordinateMode(definition);
                    referenceFrame = ResolveReferenceFrame(definition, blockRef);
                    extents = ResolveWorldExtents(definition, blockRef, coordinateMode, referenceFrame);
                    titleRegion = ResolveLocalRegion(definition.TitleRegion, effectiveBlockTransform, coordinateMode, referenceFrame);
                    numberRegion = ResolveLocalRegion(definition.DrawingNumberRegion, effectiveBlockTransform, coordinateMode, referenceFrame);
                    dateRegion = definition.DateRegion.HasArea()
                        ? ResolveLocalRegion(definition.DateRegion, effectiveBlockTransform, coordinateMode, referenceFrame)
                        : new LocalRectangle();
                    revisionRegion = definition.RevisionRegion.HasArea()
                        ? ResolveLocalRegion(definition.RevisionRegion, effectiveBlockTransform, coordinateMode, referenceFrame)
                        : new LocalRectangle();
                    phaseRegion = definition.PhaseRegion.HasArea()
                        ? ResolveLocalRegion(definition.PhaseRegion, effectiveBlockTransform, coordinateMode, referenceFrame)
                        : new LocalRectangle();
                    info1Region = definition.Info1Region.HasArea()
                        ? ResolveLocalRegion(definition.Info1Region, effectiveBlockTransform, coordinateMode, referenceFrame)
                        : new LocalRectangle();
                    info2Region = definition.Info2Region.HasArea()
                        ? ResolveLocalRegion(definition.Info2Region, effectiveBlockTransform, coordinateMode, referenceFrame)
                        : new LocalRectangle();

                    // 嵌套匹配时 ResolveLocalRegion 返回的 region 处于内层块定义空间，
                    // 而 ExtractRegionText 从外层 blockRef 定义空间起算，需统一坐标系。
                    if (isNestedMatch && coordinateMode != RegionCoordinateMode.Frame)
                    {
                        titleRegion = TransformLocalRegion(titleRegion, nestedToOuter);
                        numberRegion = TransformLocalRegion(numberRegion, nestedToOuter);
                        if (dateRegion.HasArea())
                            dateRegion = TransformLocalRegion(dateRegion, nestedToOuter);
                        if (revisionRegion.HasArea())
                            revisionRegion = TransformLocalRegion(revisionRegion, nestedToOuter);
                        if (phaseRegion.HasArea())
                            phaseRegion = TransformLocalRegion(phaseRegion, nestedToOuter);
                        if (info1Region.HasArea())
                            info1Region = TransformLocalRegion(info1Region, nestedToOuter);
                        if (info2Region.HasArea())
                            info2Region = TransformLocalRegion(info2Region, nestedToOuter);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"布局={spaceName} 块={blockName} 句柄={blockRef.Handle} 坐标解析失败: {ex.Message}");
                    continue;
                }

                if (scanWindow.HasValue && !Intersects(extents, scanWindow.Value))
                {
                    continue;
                }

                var width = extents.MaxPoint.X - extents.MinPoint.X;
                var height = extents.MaxPoint.Y - extents.MinPoint.Y;

                // 计算打印区域的 4 个实际 WCS 角点（含 BlockTransform 的缩放和旋转）
                // 不取包围盒，和矩形框扫描的 CornerPoints 同理：4 角 × WCS→DCS 只取一次包围盒
                var wcsCorners = ComputeWcsCorners(coordinateMode, referenceFrame, blockRef.BlockTransform);

                var detectedPaper = PaperSizeDetector.Detect(
                    width,
                    height,
                    PaperSizeDetector.CreateTitleBlockBatchOptions(effectivePaperToleranceMm, !layout.ModelType));
                var paper = ApplyFixedPaper(definition, detectedPaper);
                string title;
                string number;
                string date = "";
                string revision = "";
                string phase = "";
                string info1 = "";
                string info2 = "";
                try
                {
                    title = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, titleRegion, ownerTextCache);
                    number = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, numberRegion, ownerTextCache);
                    if (dateRegion.HasArea())
                        date = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, dateRegion, ownerTextCache);
                    if (revisionRegion.HasArea())
                        revision = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, revisionRegion, ownerTextCache);
                    if (phaseRegion.HasArea())
                        phase = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, phaseRegion, ownerTextCache);
                    if (info1Region.HasArea())
                        info1 = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, info1Region, ownerTextCache);
                    if (info2Region.HasArea())
                        info2 = CadTextExtractor.ExtractRegionText(tr, blockRef, owner, info2Region, ownerTextCache);
                }
                catch (Exception ex)
                {
                    warnings.Add($"布局={spaceName} 块={blockName} 句柄={blockRef.Handle} 文字提取失败: {ex.Message}");
                    title = "";
                    number = "";
                }

                // 嵌套匹配时输出诊断信息，方便排查深度嵌套场景下的字段提取问题。
                if (isNestedMatch)
                {
                    if (dateRegion.HasArea() && string.IsNullOrWhiteSpace(date))
                        warnings.Add($"布局={spaceName} 块={effectiveBlockName}(嵌套) 日期字段区域有定义但未提取到文字");
                    if (revisionRegion.HasArea() && string.IsNullOrWhiteSpace(revision))
                        warnings.Add($"布局={spaceName} 块={effectiveBlockName}(嵌套) 版次字段区域有定义但未提取到文字");
                    if (phaseRegion.HasArea() && string.IsNullOrWhiteSpace(phase))
                        warnings.Add($"布局={spaceName} 块={effectiveBlockName}(嵌套) 设计阶段字段区域有定义但未提取到文字");
                    if (info1Region.HasArea() && string.IsNullOrWhiteSpace(info1))
                        warnings.Add($"布局={spaceName} 块={effectiveBlockName}(嵌套) 信息1字段区域有定义但未提取到文字");
                    if (info2Region.HasArea() && string.IsNullOrWhiteSpace(info2))
                        warnings.Add($"布局={spaceName} 块={effectiveBlockName}(嵌套) 信息2字段区域有定义但未提取到文字");
                }

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
                    SpaceName = spaceName,
                    IsPaperSpace = !layout.ModelType,
                    BlockName = effectiveBlockName,
                    BlockHandle = blockRef.Handle.ToString(),
                    MatchIndex = matchIndex++,
                    DrawingNumber = number,
                    Title = title,
                    Date = date,
                    Revision = revision,
                    Phase = phase,
                    Info1 = info1,
                    Info2 = info2,
                    CadDrawingNumber = number,
                    CadTitle = title,
                    CadDate = date,
                    CadRevision = revision,
                    CadPhase = phase,
                    CadInfo1 = info1,
                    CadInfo2 = info2,
                    PaperName = paper.PaperName,
                    ScaleText = paper.ScaleText,
                    SizeText = $"{Math.Abs(width):0.##} x {Math.Abs(height):0.##}",
                    PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm",
                    DetectionNote = $"{boundaryNote}; {paper.Note}",
                    PaperWidthMm = paper.PaperWidthMm,
                    PaperHeightMm = paper.PaperHeightMm,
                    DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    RequiresCustomPaperRegistration = paper.RequiresCustomPaper,
                    MinX = extents.MinPoint.X,
                    MinY = extents.MinPoint.Y,
                    MaxX = extents.MaxPoint.X,
                    MaxY = extents.MaxPoint.Y,
                    CornerPoints = wcsCorners
                });
            }
        }

        tr.Commit();
        LogScanWarnings(sourceName, warnings);
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
            var duplicateIndex = result.FindIndex(existing => IsSameScanSpace(existing, job) && GetOverlapRatio(existing, job) >= 0.9);
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

    private static bool IsSameScanSpace(PlotJob a, PlotJob b)
    {
        return string.Equals(a.SourceFile, b.SourceFile, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.SpaceName, b.SpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldScanLayout(Layout layout, BlockTableRecord owner, TitleBlockScanScope scope, string? currentSpaceName)
    {
        switch (scope)
        {
            case TitleBlockScanScope.PaperLayouts:
                return !layout.ModelType;
            case TitleBlockScanScope.ModelSpace:
                return layout.ModelType;
            case TitleBlockScanScope.CurrentSpace:
                return IsCurrentSpace(layout, owner, currentSpaceName);
            case TitleBlockScanScope.AllSpaces:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCurrentSpace(Layout layout, BlockTableRecord owner, string? currentSpaceName)
    {
        if (string.IsNullOrWhiteSpace(currentSpaceName))
        {
            return true;
        }

        return string.Equals(layout.LayoutName, currentSpaceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(owner.Name, currentSpaceName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetCurrentSpaceName(Database db)
    {
        try
        {
            var layoutName = LayoutManager.Current.CurrentLayout;
            if (!string.IsNullOrWhiteSpace(layoutName))
            {
                return layoutName;
            }
        }
        catch
        {
        }

        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var current = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
            if (current.IsLayout)
            {
                var layout = (Layout)tr.GetObject(current.LayoutId, OpenMode.ForRead);
                tr.Commit();
                return layout.LayoutName;
            }

            tr.Commit();
        }
        catch
        {
        }

        return null;
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
        var hasSavedFrame = HasArea(definition.PrintRegion);
        LocalRectangle blockFrame;
        try
        {
            blockFrame = TransformExtents(blockRef.GeometricExtents, blockRef.BlockTransform.Inverse());
        }
        catch
        {
            // AutoCAD can throw eInvalidExtents for valid block references whose
            // graphics extents have not been generated. A manually selected print
            // boundary is already stored in block-local coordinates and is the
            // authoritative frame in this case.
            if (hasSavedFrame)
            {
                return definition.PrintRegion;
            }

            throw;
        }

        if (hasSavedFrame)
        {
            return HasMeaningfulOverlap(definition.PrintRegion, blockFrame)
                ? definition.PrintRegion
                : blockFrame;
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

    private static LocalRectangle TransformLocalRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
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

        if (ShouldPreferDetectedLongPaper(name, definition, detected))
        {
            return new PaperDetection
            {
                PaperName = detected.PaperName,
                ScaleText = detected.ScaleText,
                ScaleValue = detected.ScaleValue,
                IsLong = detected.IsLong,
                PaperWidthMm = detected.PaperWidthMm,
                PaperHeightMm = detected.PaperHeightMm,
                RequiresCustomPaper = detected.RequiresCustomPaper,
                Note = $"图框边界识别为加长图，已优先使用自动图幅；图框库默认纸张 {name} 仅作为新增图框默认值。{detected.Note}"
            };
        }

        return new PaperDetection
        {
            PaperName = name,
            ScaleText = detected.ScaleText,
            ScaleValue = detected.ScaleValue,
            IsLong = IsLongPaperName(name),
            PaperWidthMm = definition.PaperWidthMm,
            PaperHeightMm = definition.PaperHeightMm,
            Note = $"固定纸张来自图框库，输出纸张 {definition.PaperWidthMm:0.##} x {definition.PaperHeightMm:0.##} mm；比例按图框边界自动识别为 {detected.ScaleText}"
        };
    }

    private static bool ShouldPreferDetectedLongPaper(string libraryPaperName, TitleBlockDefinition definition, PaperDetection detected)
    {
        if (!detected.IsLong || detected.PaperWidthMm <= 0 || detected.PaperHeightMm <= 0)
        {
            return false;
        }

        // 任意加长图必须保留实测物理尺寸；图框库中的固定模数纸张不能覆盖动态纸张。
        if (detected.RequiresCustomPaper)
        {
            return true;
        }

        if (!IsLongPaperName(libraryPaperName))
        {
            return true;
        }

        var directError = Math.Max(
            Math.Abs(definition.PaperWidthMm - detected.PaperWidthMm),
            Math.Abs(definition.PaperHeightMm - detected.PaperHeightMm));
        var rotatedError = Math.Max(
            Math.Abs(definition.PaperWidthMm - detected.PaperHeightMm),
            Math.Abs(definition.PaperHeightMm - detected.PaperWidthMm));
        return Math.Min(directError, rotatedError) > 10d;
    }

    private static bool IsLongPaperName(string paperName)
    {
        return paperName.IndexOf('+') > 0;
    }

    private enum RegionCoordinateMode
    {
        Local,
        World,
        Frame
    }

    private static void LogScanWarnings(string sourceName, IReadOnlyCollection<string> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
            var path = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "scan_debug.log");
            var lines = new List<string>
            {
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [SCAN_WARN] 文件={sourceName} 跳过对象={warnings.Count}"
            };
            lines.AddRange(warnings.Select(x => "  " + x));
            File.AppendAllLines(path, lines);
        }
        catch
        {
        }
    }

    /// <summary>
    /// When a top-level block reference doesn't directly match the library, recursively
    /// search nested blocks up to 6 levels deep for a visible inner block that matches.
    /// </summary>
    private static TitleBlockDefinition? ResolveNestedLibraryMatch(
        Transaction tr,
        BlockReference outerRef,
        TitleBlockLibrary library,
        out Matrix3d nestedTransform)
    {
        nestedTransform = Matrix3d.Identity;

        var definitionId = outerRef.BlockTableRecord;
        if (definitionId.IsNull)
        {
            return null;
        }

        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        return ResolveNestedLibraryMatchRecursive(tr, definition, Matrix3d.Identity, library, out nestedTransform, new HashSet<ObjectId>(), 0);
    }

    private static TitleBlockDefinition? ResolveNestedLibraryMatchRecursive(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d accumulatedTransform,
        TitleBlockLibrary library,
        out Matrix3d nestedTransform,
        ISet<ObjectId> visited,
        int depth)
    {
        nestedTransform = Matrix3d.Identity;
        if (depth > 6 || !visited.Add(definition.ObjectId))
        {
            return null;
        }

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference nested)
            {
                continue;
            }

            if (!IsEntityVisible(nested))
            {
                continue;
            }

            string nestedName;
            try
            {
                nestedName = CadTextExtractor.GetBlockName(nested, tr);
            }
            catch
            {
                continue;
            }

            var match = library.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, nestedName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                nestedTransform = nested.BlockTransform * accumulatedTransform;
                return match;
            }

            // 继续向更深层搜索
            try
            {
                var nestedDef = (BlockTableRecord)tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                var innerTransform = nested.BlockTransform * accumulatedTransform;
                var deeper = ResolveNestedLibraryMatchRecursive(tr, nestedDef, innerTransform, library, out var deeperTransform, visited, depth + 1);
                if (deeper != null)
                {
                    nestedTransform = deeperTransform;
                    return deeper;
                }
            }
            catch
            {
            }
        }

        visited.Remove(definition.ObjectId);
        return null;
    }

    /// <summary>
    /// 计算打印区域的 4 个实际 WCS 角点（不取包围盒），用于 DCS 四点法变换。
    /// 和矩形框扫描的 CornerPoints 同理：4 角 × WCS→DCS 只取一次包围盒，
    /// 避免在 ResolveWorldExtents 中已经被取过一次包围盒的值再次被取包围盒。
    /// </summary>
    private static double[] ComputeWcsCorners(
        RegionCoordinateMode mode, LocalRectangle frame, Matrix3d blockTransform)
    {
        // World 模式：PrintRegion 本身是 WCS 坐标，无需再乘 BlockTransform
        var xform = mode == RegionCoordinateMode.World ? Matrix3d.Identity : blockTransform;

        var c = new[]
        {
            new Point3d(frame.MinX, frame.MinY, 0).TransformBy(xform),
            new Point3d(frame.MaxX, frame.MinY, 0).TransformBy(xform),
            new Point3d(frame.MaxX, frame.MaxY, 0).TransformBy(xform),
            new Point3d(frame.MinX, frame.MaxY, 0).TransformBy(xform)
        };
        return new[] { c[0].X, c[0].Y, c[1].X, c[1].Y, c[2].X, c[2].Y, c[3].X, c[3].Y };
    }

    private static bool IsEntityVisible(Entity entity)
    {
        try
        {
            return entity.Visible;
        }
        catch
        {
            return true;
        }
    }
}
