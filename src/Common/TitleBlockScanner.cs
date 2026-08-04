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
        var storedSettings = AppSettingsStore.Load();
        var effectivePaperToleranceMm = paperMatchToleranceMm ?? storedSettings.PaperMatchToleranceMm;
        if (string.IsNullOrWhiteSpace(sourceName))
        {
            sourceName = db.Filename;
        }

        using var tr = db.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

        var libraryBlockNames = new HashSet<string>(
            library.Blocks.SelectMany(x => ExpandLibraryNameParts(x.BlockName)).Where(name => !string.IsNullOrWhiteSpace(name)),
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
                ObjectId frameDefinitionId = blockRef.BlockTableRecord;
                // 嵌套匹配时需记录从内层块定义到外层块定义空间的累积变换，用于后续 region 坐标对齐。
                Matrix3d nestedToOuter = Matrix3d.Identity;
                bool isNestedMatch = false;
                if (definition == null)
                {
                    definition = ResolveNestedLibraryMatch(
                        tr,
                        blockRef,
                        blockName,
                        library,
                        out var nestedTransform,
                        out var nestedDefinitionId);
                    if (definition != null)
                    {
                        effectiveBlockTransform = nestedTransform * blockRef.BlockTransform;
                        effectiveBlockName = definition.BlockName;
                        nestedToOuter = nestedTransform;
                        frameDefinitionId = nestedDefinitionId;
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
                    referenceFrame = ResolveReferenceFrame(
                        tr,
                        definition,
                        blockRef,
                        frameDefinitionId,
                        effectiveBlockTransform,
                        coordinateMode);
                    extents = ResolveWorldExtents(definition, blockRef, effectiveBlockTransform, coordinateMode, referenceFrame);
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
                    // 而 ExtractRegionText 从外层 blockRef 定义空间起算，三种坐标模式都需统一坐标系。
                    if (isNestedMatch)
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

                // 识别与显示不能用 extents 包围盒宽高——块参照带旋转角时包围盒比实际边长大（45° 约放大 √2 倍），
                // 会误判比例和图幅。与矩形框扫描同理，识别和显示改用 wcsCorners 相邻角点的实际边长。

                // 计算打印区域的 4 个实际 WCS 角点（含 BlockTransform 的缩放和旋转）
                // 不取包围盒，和矩形框扫描的 CornerPoints 同理：4 角 × WCS→DCS 只取一次包围盒
                var wcsCorners = ComputeWcsCorners(coordinateMode, referenceFrame, effectiveBlockTransform);
                var width = CornerDistance(wcsCorners, 0, 1);
                var height = CornerDistance(wcsCorners, 1, 2);

                var detectionOptions = PaperSizeDetector.CreateTitleBlockBatchOptions(effectivePaperToleranceMm, !layout.ModelType, storedSettings.LongPaperSnapToleranceMm);
                if (IsGenericDynamicPaperName(definition.PaperName))
                {
                    // A2+ 中的 A2 是录入时已经确认的基础图幅，扫描只允许重新计算长边。
                    // 比例由“录入打印范围 CAD 尺寸 / 录入纸张毫米尺寸”反推，不能再套模型空间默认 1:100。
                    detectionOptions.PreferredPaperBaseName = GetGenericDynamicPaperBaseName(definition.PaperName);
                    var recordedScale = InferRecordedPaperScale(definition);
                    if (recordedScale > 0)
                    {
                        detectionOptions.PreferredScaleValue = recordedScale;
                    }
                }

                // 固定图幅可用入库尺寸消除图框零头误差；A1+/A2+ 是可自由拉伸模板，
                // 入库宽高只代表录入时那一个实例，绝不能参与扫描候选排序。
                if (!IsGenericDynamicPaperName(definition.PaperName)
                    && definition.PaperWidthMm > 0
                    && definition.PaperHeightMm > 0)
                {
                    detectionOptions.PreferredPaperWidthMm = definition.PaperWidthMm;
                    detectionOptions.PreferredPaperHeightMm = definition.PaperHeightMm;
                }
                var detectedPaper = PaperSizeDetector.Detect(
                    width,
                    height,
                    detectionOptions);
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

                // 与图框录入共用同一套最大闭合矩形/线包围盒规则，同时把真实源实体句柄交给打印阶段临时移层。
                BlockFrameGeometry.TryGetFrame(
                    tr,
                    frameDefinitionId,
                    out _,
                    out _,
                    out var frameBoundaryHandles);

                jobs.Add(new PlotJob
                {
                    SourceFile = sourceName,
                    SpaceName = spaceName,
                    IsPaperSpace = !layout.ModelType,
                    BlockName = effectiveBlockName,
                    BlockHandle = blockRef.Handle.ToString(),
                    FrameBoundaryHandles = frameBoundaryHandles,
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

        if (string.Equals(
                definition.CoordinateMode,
                TitleBlockDefinition.DynamicRightBottomCoordinateMode,
                StringComparison.OrdinalIgnoreCase))
        {
            return RegionCoordinateMode.FrameRightBottomDynamic;
        }

        return string.Equals(definition.CoordinateMode, "World", StringComparison.OrdinalIgnoreCase)
            ? RegionCoordinateMode.World
            : RegionCoordinateMode.Local;
    }

    private static Extents3d ResolveWorldExtents(TitleBlockDefinition definition, BlockReference blockRef, Matrix3d effectiveBlockTransform, RegionCoordinateMode mode, LocalRectangle referenceFrame)
    {
        if (mode == RegionCoordinateMode.World && definition.HasPrintRegion)
        {
            return ToExtents(definition.PrintRegion);
        }

        if (mode == RegionCoordinateMode.Frame || mode == RegionCoordinateMode.FrameRightBottomDynamic)
        {
            return TransformRegion(referenceFrame, effectiveBlockTransform);
        }

        return definition.HasPrintRegion
            ? TransformRegion(definition.PrintRegion, effectiveBlockTransform)
            : blockRef.GeometricExtents;
    }

    private static LocalRectangle ResolveLocalRegion(LocalRectangle region, Matrix3d blockTransform, RegionCoordinateMode mode, LocalRectangle referenceFrame)
    {
        if (mode == RegionCoordinateMode.Frame)
        {
            return OffsetRegion(region, referenceFrame.MinX, referenceFrame.MinY);
        }


        if (mode == RegionCoordinateMode.FrameRightBottomDynamic)
        {
            return OffsetRegion(region, referenceFrame.MaxX, referenceFrame.MinY);
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

    private static LocalRectangle ResolveReferenceFrame(
        Transaction tr,
        TitleBlockDefinition definition,
        BlockReference blockRef,
        ObjectId frameDefinitionId,
        Matrix3d effectiveBlockTransform,
        RegionCoordinateMode mode)
    {
        var hasSavedFrame = HasArea(definition.PrintRegion);
        if (mode == RegionCoordinateMode.FrameRightBottomDynamic)
        {
            // 可拉伸模板的录入 PrintRegion 只是回退值。每个块参照都必须从当前求值定义重新取外框。
            // 若外层还带 A1/A2/A3 可见性切换，前面的嵌套匹配已先选定当前可见内层定义。
            if (BlockFrameGeometry.TryGetFrame(
                    tr,
                    frameDefinitionId,
                    out var liveFrame,
                    out _,
                    out _))
            {
                return liveFrame;
            }

            if (hasSavedFrame)
            {
                return definition.PrintRegion;
            }
        }

        LocalRectangle blockFrame;
        try
        {
            // blockFrame 必须换算到与 PrintRegion 相同的坐标系（入库时的复合变换基准），
            // 否则嵌套动态块下两者不在同一坐标系，重叠比较没有意义。
            blockFrame = TransformExtents(blockRef.GeometricExtents, effectiveBlockTransform.Inverse());
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

        // A1+ 类不带具体分数的名称是“可变长度模板”，不是固定纸张。
        // 库中的宽高只用于录入回显，批打时必须完整使用当前外框的检测结果。
        if (IsGenericDynamicPaperName(name))
        {
            return detected;
        }

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

    private static bool IsGenericDynamicPaperName(string paperName)
    {
        return !string.IsNullOrWhiteSpace(paperName)
               && paperName.EndsWith("+", StringComparison.Ordinal);
    }

    private static string GetGenericDynamicPaperBaseName(string paperName)
    {
        return IsGenericDynamicPaperName(paperName)
            ? paperName.Substring(0, paperName.Length - 1)
            : "";
    }

    /// <summary>
    /// 图框库没有单独保存比例字段；可拉伸模板可由录入外框尺寸和录入纸张尺寸稳定反推比例。
    /// 同时比较同向和宽高互换两种解释，以兼容横向/纵向图框。
    /// </summary>
    private static double InferRecordedPaperScale(TitleBlockDefinition definition)
    {
        if (!definition.PrintRegion.HasArea()
            || definition.PaperWidthMm <= 0
            || definition.PaperHeightMm <= 0)
        {
            return 0;
        }

        var frameWidth = Math.Abs(definition.PrintRegion.MaxX - definition.PrintRegion.MinX);
        var frameHeight = Math.Abs(definition.PrintRegion.MaxY - definition.PrintRegion.MinY);
        var directX = frameWidth / definition.PaperWidthMm;
        var directY = frameHeight / definition.PaperHeightMm;
        var swappedX = frameWidth / definition.PaperHeightMm;
        var swappedY = frameHeight / definition.PaperWidthMm;

        var directError = RelativeScaleDifference(directX, directY);
        var swappedError = RelativeScaleDifference(swappedX, swappedY);
        var scale = directError <= swappedError
            ? (directX + directY) / 2d
            : (swappedX + swappedY) / 2d;
        return double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 ? 0 : scale;
    }

    private static double RelativeScaleDifference(double left, double right)
    {
        var denominator = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), 1e-9d);
        return Math.Abs(left - right) / denominator;
    }

    private enum RegionCoordinateMode
    {
        Local,
        World,
        Frame,
        FrameRightBottomDynamic
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
    /// <summary>
    /// 图框库块名可能是“外层+内层”复合名：文字缓存按外层参照名过滤，
    /// 需要完整名和各分段都能命中，因此把复合名拆开后一起返回。
    /// </summary>
    private static IEnumerable<string> ExpandLibraryNameParts(string? blockName)
    {
        var fullName = blockName ?? "";
        if (string.IsNullOrWhiteSpace(fullName))
        {
            yield break;
        }

        yield return fullName;
        foreach (var part in fullName.Split('+'))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    private static TitleBlockDefinition? ResolveNestedLibraryMatch(
        Transaction tr,
        BlockReference outerRef,
        string outerBlockName,
        TitleBlockLibrary library,
        out Matrix3d nestedTransform,
        out ObjectId matchedDefinitionId)
    {
        nestedTransform = Matrix3d.Identity;
        matchedDefinitionId = ObjectId.Null;

        var definitionId = outerRef.BlockTableRecord;
        if (definitionId.IsNull)
        {
            return null;
        }

        var definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        return ResolveNestedLibraryMatchRecursive(
            tr,
            definition,
            Matrix3d.Identity,
            outerBlockName,
            library,
            out nestedTransform,
            out matchedDefinitionId,
            new HashSet<ObjectId>(),
            0);
    }

    private static TitleBlockDefinition? ResolveNestedLibraryMatchRecursive(
        Transaction tr,
        BlockTableRecord definition,
        Matrix3d accumulatedTransform,
        string outerBlockName,
        TitleBlockLibrary library,
        out Matrix3d nestedTransform,
        out ObjectId matchedDefinitionId,
        ISet<ObjectId> visited,
        int depth)
    {
        nestedTransform = Matrix3d.Identity;
        matchedDefinitionId = ObjectId.Null;
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

            // 新版“外层+内层”复合名只在第一层嵌套匹配，优先于旧版图框库的纯内层名记录。
            var match = depth == 0 && !string.IsNullOrWhiteSpace(outerBlockName)
                ? library.Blocks.FirstOrDefault(x =>
                    string.Equals(x.BlockName, outerBlockName + "+" + nestedName, StringComparison.OrdinalIgnoreCase))
                : null;
            match ??= library.Blocks.FirstOrDefault(x =>
                string.Equals(x.BlockName, nestedName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                nestedTransform = nested.BlockTransform * accumulatedTransform;
                matchedDefinitionId = nested.BlockTableRecord;
                return match;
            }

            // 继续向更深层搜索
            try
            {
                var nestedDef = (BlockTableRecord)tr.GetObject(nested.BlockTableRecord, OpenMode.ForRead);
                var innerTransform = nested.BlockTransform * accumulatedTransform;
                var deeper = ResolveNestedLibraryMatchRecursive(
                    tr,
                    nestedDef,
                    innerTransform,
                    outerBlockName,
                    library,
                    out var deeperTransform,
                    out var deeperDefinitionId,
                    visited,
                    depth + 1);
                if (deeper != null)
                {
                    nestedTransform = deeperTransform;
                    matchedDefinitionId = deeperDefinitionId;
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
    /// <summary>角点数组（x0,y0,…,x3,y3）中两个角点的平面距离。</summary>
    private static double CornerDistance(double[] corners, int fromIndex, int toIndex)
    {
        var dx = corners[toIndex * 2] - corners[fromIndex * 2];
        var dy = corners[toIndex * 2 + 1] - corners[fromIndex * 2 + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

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
