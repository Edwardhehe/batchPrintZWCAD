using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static class PlotterService
{
    private const double ExactMediaToleranceMm = 0.05d;

    public sealed class PlotJobResult
    {
        public PlotJob Job { get; set; } = new();
        public Exception? Error { get; set; }
        public bool Succeeded => Error == null;
    }

    private sealed class MediaSelection
    {
        public string Name { get; set; } = "";
        public bool NeedsRotation { get; set; }
    }

    private static readonly object MediaNameCacheLock = new();
    private static readonly Dictionary<string, IReadOnlyList<string>> MediaNameCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static List<PlotJobResult> PlotMany(
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PlotJobResult>();
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;

        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        try
        {
            foreach (var group in jobs.GroupBy(job => GetPlotGroupKey(job, currentDocument, settings)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var groupJobs = group.ToList();
                try
                {
                    if (group.Key == "__CURRENT__")
                    {
                        PlotCurrentDocumentGroup(groupJobs, currentDocument, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                        continue;
                    }

                    if (group.Key.StartsWith("__DB__:", StringComparison.OrdinalIgnoreCase))
                    {
                        PlotSideDatabaseGroup(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                        continue;
                    }

                    PlotOpenedDocumentGroup(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, settings, beforeJob, results, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    foreach (var job in groupJobs.Where(job => !results.Any(x => ReferenceEquals(x.Job, job))))
                    {
                        results.Add(new PlotJobResult { Job = job, Error = ex });
                    }
                }
            }
        }
        finally
        {
            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }

        return results;
    }

    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument, AppSettings settings)
    {
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            using (currentDocument.LockDocument())
            {
                ActivateLayout(job);
                RefreshJobWindowFromOpenedDocument(currentDocument.Database, job);
                PlotDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, settings, currentDocument);
            }

            return;
        }

        if (!settings.OpenExternalDwgForPlot)
        {
            using var db = new Database(false, true);
            db.ReadDwgFile(job.SourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
            db.CloseInput(true);
            db.ResolveXrefs(true, false);
            PlotDatabase(db, Path.GetFileName(job.SourceFile), job, deviceName, styleSheet, settings);
            return;
        }

        PlotOpenedDocument(job, deviceName, styleSheet, settings);
    }

    public static void Preview(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var settings = AppSettingsStore.Load();
        EnsureTextGeometryMode(deviceName, settings.ConvertTextToGeometryWhenPlotting);
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var doc = IsCurrentDocumentJob(job, currentDocument) ? currentDocument : FindOpenDocument(job.SourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(job.SourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            using (doc.LockDocument())
            {
                ActivateLayout(job);
                // 首次扫描得到的图框信息已可用于预览，避免每次点击预览都重新扫描整张图纸。
                PrepareEditorViewForPlot(doc, job);
                PreviewDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
            }
        }
        finally
        {
            if (shouldClose)
            {
                TryCloseWithoutSave(doc);
            }

            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }
    }

    private static string GetPlotGroupKey(PlotJob job, Document currentDocument, AppSettings settings)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        var file = string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
        return settings.OpenExternalDwgForPlot ? file : "__DB__:" + file;
    }

    private static void EnsureTextGeometryMode(string deviceName, bool convertToGeometry)
    {
        WaitForPlotIdle();
        var result = AcadPlotterInstaller.ApplyTextGeometryMode(deviceName, convertToGeometry);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    private static void PlotCurrentDocumentGroup(
        IReadOnlyList<PlotJob> jobs,
        Document currentDocument,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        CadApp.DocumentManager.MdiActiveDocument = currentDocument;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                using (currentDocument.LockDocument())
                {
                    ActivateLayout(job);
                    RefreshJobWindowFromOpenedDocument(currentDocument.Database, job);
                    PrepareEditorViewForPlot(currentDocument, job);
                    PlotDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, settings, currentDocument);
                }

                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    private static void PlotOpenedDocumentGroup(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        var doc = FindOpenDocument(sourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(sourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    beforeJob?.Invoke(job);
                    using (doc.LockDocument())
                    {
                        ActivateLayout(job);
                        RefreshJobWindowFromOpenedDocument(doc.Database, job);
                        PrepareEditorViewForPlot(doc, job);
                        PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
                    }

                    results.Add(new PlotJobResult { Job = job });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results.Add(new PlotJobResult { Job = job, Error = ex });
                }
            }
        }
        finally
        {
            if (shouldClose)
            {
                TryCloseWithoutSave(doc);
            }
        }
    }

    private static void PlotSideDatabaseGroup(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        AppSettings settings,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results,
        CancellationToken cancellationToken)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        db.ResolveXrefs(true, false);

        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                beforeJob?.Invoke(job);
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, settings);
                results.Add(new PlotJobResult { Job = job });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    private static void PlotOpenedDocument(PlotJob job, string deviceName, string styleSheet, AppSettings settings)
    {
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var doc = FindOpenDocument(job.SourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(job.SourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            using (doc.LockDocument())
            {
                ActivateLayout(job);
                RefreshJobWindowFromOpenedDocument(doc.Database, job);
                PrepareEditorViewForPlot(doc, job);
                PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
            }
        }
        finally
        {
            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }

            if (shouldClose)
            {
                TryCloseWithoutSave(doc);
            }
        }
    }

    private static void ActivateLayout(PlotJob job)
    {
        if (string.IsNullOrWhiteSpace(job.SpaceName))
        {
            return;
        }

        try
        {
            LayoutManager.Current.CurrentLayout = job.SpaceName;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法激活目标布局“{job.SpaceName}”，已停止打印以避免输出错误区域。", ex);
        }
    }

    private static void RefreshJobWindowFromOpenedDocument(Database db, PlotJob job)
    {
        if (job.IsManualWindow)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load();
        var candidates = TitleBlockScanner.Scan(db, library, job.SourceFile)
            .Where(x => string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var refreshed = candidates.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(job.BlockHandle)
                && string.Equals(x.BlockHandle, job.BlockHandle, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(x =>
                string.Equals(x.DrawingNumber, job.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Title, job.Title, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(x => x.MatchIndex == job.MatchIndex);

        if (refreshed == null)
        {
            throw new InvalidOperationException(
                $"重新打开图纸后未找到原图框。布局={job.SpaceName}，块={job.BlockName}，句柄={job.BlockHandle}。请重新扫描图纸后再打印。");
        }

        job.MinX = refreshed.MinX;
        job.MinY = refreshed.MinY;
        job.MaxX = refreshed.MaxX;
        job.MaxY = refreshed.MaxY;
        job.IsDcsWindow = false;  // 重新扫描后坐标回到 WCS，清除 DCS 标记
        job.PaperName = refreshed.PaperName;
        job.ScaleText = refreshed.ScaleText;
        job.SizeText = refreshed.SizeText;
        job.PaperSizeText = refreshed.PaperSizeText;
        job.PaperWidthMm = refreshed.PaperWidthMm;
        job.PaperHeightMm = refreshed.PaperHeightMm;
        job.FrameBoundaryHandles = refreshed.FrameBoundaryHandles == null
            ? null
            : (string[])refreshed.FrameBoundaryHandles.Clone();
    }

    private static Document? FindOpenDocument(string file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(file);
        foreach (Document doc in CadApp.DocumentManager)
        {
            var docFile = doc.Database.Filename;
            if (!string.IsNullOrWhiteSpace(docFile)
                && string.Equals(Path.GetFullPath(docFile), fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return doc;
            }
        }

        return null;
    }

    private static bool IsCurrentDocumentJob(PlotJob job, Document currentDocument)
    {
        var currentFile = currentDocument.Database.Filename;
        if (string.IsNullOrWhiteSpace(currentFile))
        {
            return string.Equals(job.SourceFile, currentDocument.Name, StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrWhiteSpace(job.SourceFile))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(job.SourceFile), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
    }

    private static void PlotDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, AppSettings settings, Document? plotDocument = null)
    {
        WaitForPlotIdle();

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var frameLayerApplied = settings.HideFrameBoundaryWhenPlotting
                && TemporaryFramePlotLayer.Apply(tr, db, job);
            var layout = FindLayoutForJob(tr, db, job);
            using var plotSettings = new PlotSettings(layout.ModelType);
            plotSettings.CopyFrom(layout);
            var plotWindow = GetPlotWindow(job, plotDocument);

            var validator = PlotSettingsValidator.Current;
            var media = job.RequireExactPaperSize
                ? TrySelectExactSingleMediaWithoutRefresh(
                    validator, plotSettings, job, settings, deviceName, layout.ModelType)
                : null;
            if (media == null && HasCachedMediaNames(deviceName, layout.ModelType))
            {
                // 已有同一 PC5/PMP 的纸张目录时，只绑定设备，不再卸载设备和刷新全部列表。
                validator.SetPlotConfigurationName(plotSettings, deviceName, null);
            }
            else if (media == null)
            {
                // 首次使用或配置文件变化后强制重新读取 PMP，随后缓存纸张目录。
                validator.SetPlotConfigurationName(plotSettings, "None", null);
                validator.SetPlotConfigurationName(plotSettings, deviceName, null);
                validator.RefreshLists(plotSettings);
            }
            TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);

            media ??= SelectMedia(validator, plotSettings, job, settings, deviceName, layout.ModelType, plotWindow);
            if (media == null)
            {
                var allMedia = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
                var debugInfo = string.Join("|", allMedia.Where(x => x.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf("UserDefined", StringComparison.OrdinalIgnoreCase) >= 0));
                throw new InvalidOperationException(
                    $"未找到匹配 {job.PaperSizeText} 的输出纸张（{job.PaperWidthMm:0.##}x{job.PaperHeightMm:0.##}mm, name={job.PaperName}）。自定义纸张列表: {debugInfo}");
            }

            validator.SetCanonicalMediaName(plotSettings, media.Name);
            EnsureExactMediaSize(plotSettings, job);
            if (!string.IsNullOrWhiteSpace(styleSheet))
            {
                validator.SetCurrentStyleSheet(plotSettings, styleSheet);
            }

            validator.SetPlotWindowArea(plotSettings, plotWindow);
            validator.SetPlotType(plotSettings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
            ConfigurePlotScale(validator, plotSettings, plotWindow, job);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media, job, plotWindow, deviceName));

            var plotInfo = new PlotInfo
            {
                Layout = layout.ObjectId,
                OverrideSettings = plotSettings
            };
            var plotInfoValidator = new PlotInfoValidator
            {
                MediaMatchingPolicy = MatchingPolicy.MatchEnabled
            };
            plotInfoValidator.Validate(plotInfo);

            PrepareOutputFile(job.OutputPath);
            RunPlot(plotInfo, documentName, job.OutputPath, job.DrawingNumber);

            // 临时移层与绘图处在同一事务；绘图结束后不提交即可原子恢复实体和临时图层。
            if (!frameLayerApplied)
            {
                tr.Commit();
            }
            WaitForPlotIdle();
            ValidatePlotOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    private static Layout FindLayoutForJob(Transaction tr, Database db, PlotJob job)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var availableLayouts = new List<string>();
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            availableLayouts.Add(layout.LayoutName);
            if (string.Equals(owner.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        throw new InvalidOperationException(
            $"未找到目标布局“{job.SpaceName}”。可用布局: {string.Join(", ", availableLayouts)}。请重新扫描图纸。");
    }

    private static void ConfigurePlotScale(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        Extents2d window,
        PlotJob job)
    {
        if (!job.LeavePaperMargin)
        {
            if (job.UseExactWindowScale)
            {
                SetExactWindowScale(validator, plotSettings, window);
                return;
            }

            validator.SetUseStandardScale(plotSettings, true);
            validator.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
            return;
        }

        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        if (windowWidth <= 0d || windowHeight <= 0d)
            throw new InvalidOperationException("打印窗口尺寸无效，无法计算打印比例。");

        if (job.PaperMarginMm > 0d)
        {
            // ── 扩大纸张模式 ──
            // 纸张已被扩大为 PaperWidthMm+margin*2 × PaperHeightMm+margin*2。
            // 比例按原始图框尺寸（未扩大）计算，使内容居中、四周留白均等。
            var originalShortMm = Math.Min(job.PaperWidthMm, job.PaperHeightMm);
            var windowShortSide = Math.Min(windowWidth, windowHeight);
            if (originalShortMm <= 0d || windowShortSide <= 0d)
                throw new InvalidOperationException("纸张或打印窗口尺寸无效，无法计算扩大纸张留白比例。");
            var scale = originalShortMm / windowShortSide;
            validator.SetUseStandardScale(plotSettings, false);
            validator.SetCustomPrintScale(plotSettings, new CustomScale(scale, 1d));
            return;
        }

        // ── 缩比例模式（PaperMarginMm < 0 时 abs 取值） ──
        var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
        var paperSize = plotSettings.PlotPaperSize;
        var paperShortSide = Math.Min(paperSize.X, paperSize.Y);
        var windowShort = Math.Min(windowWidth, windowHeight);
        var usableShortSide = paperShortSide - marginMm * 2d;

        if (usableShortSide <= 0d)
            throw new InvalidOperationException("留白值过大导致可用纸张面积为零，请减小留白距离。");

        // 保持原图框窗口不变，只缩小打印比例。扩大窗口会把图框外的相邻对象带入 PDF。
        var scaleReduced = usableShortSide / windowShort;
        validator.SetUseStandardScale(plotSettings, false);
        validator.SetCustomPrintScale(plotSettings, new CustomScale(scaleReduced, 1d));
    }

    private static void SetExactWindowScale(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        Extents2d window)
    {
        var paper = plotSettings.PlotPaperSize;
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        var paperLong = Math.Max(paper.X, paper.Y);
        var paperShort = Math.Min(paper.X, paper.Y);
        var windowLong = Math.Max(windowWidth, windowHeight);
        var windowShort = Math.Min(windowWidth, windowHeight);
        if (paperShort <= 0d || windowShort <= 0d)
            throw new InvalidOperationException("任意纸张或打印窗口尺寸无效，无法计算精确打印比例。");

        var scale = Math.Min(paperLong / windowLong, paperShort / windowShort);
        validator.SetUseStandardScale(plotSettings, false);
        validator.SetCustomPrintScale(plotSettings, new CustomScale(scale, 1d));
    }

    private static Extents2d GetPlotWindow(PlotJob job, Document? plotDocument)
    {
        // 单张打印已在 BatchPlotCommands.SinglePlotCore 完成 UCS→DCS 全链路变换
        // 此处直接返回坐标，跳过 WCS→DCS 二次变换和 GetWorldToDisplayMatrix
        if (job.IsDcsWindow)
        {
            return new Extents2d(
                Math.Min(job.MinX, job.MaxX),
                Math.Min(job.MinY, job.MaxY),
                Math.Max(job.MinX, job.MaxX),
                Math.Max(job.MinY, job.MaxY));
        }

        if (job.IsPaperSpace)
        {
            return new Extents2d(
                Math.Min(job.MinX, job.MaxX),
                Math.Min(job.MinY, job.MaxY),
                Math.Max(job.MinX, job.MaxX),
                Math.Max(job.MinY, job.MaxY));
        }

        if (plotDocument != null)
        {
            try
            {
                var view = plotDocument.Editor.GetCurrentView();
                var worldToDisplay = GetWorldToDisplayMatrix(view);
                // UCS 任务必须从保存的 UCS 矩形重建真实 WCS 四角，不能使用 WCS 包围盒四角。
                var points = CadSelectionWindow.GetJobWorldCorners(job)
                    .Select(point => point.TransformBy(worldToDisplay))
                    .ToArray();

                return new Extents2d(
                    points.Min(p => p.X),
                    points.Min(p => p.Y),
                    points.Max(p => p.X),
                    points.Max(p => p.Y));
            }
            catch
            {
                // Some side databases and layout states cannot expose a reliable editor view.
                // In that case the plot API falls back to raw layout/model coordinates.
            }
        }

        return new Extents2d(
            Math.Min(job.MinX, job.MaxX),
            Math.Min(job.MinY, job.MaxY),
            Math.Max(job.MinX, job.MaxX),
            Math.Max(job.MinY, job.MaxY));
    }

    private static Matrix3d GetWorldToDisplayMatrix(ViewTableRecord view)
    {
        var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
        matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
        matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
        return matrix.Inverse();
    }

    private static void PrepareEditorViewForPlot(Document doc, PlotJob job)
    {
        // 图纸空间无视图概念，跳过。
        // 已生成 DCS 的任务必须保留生成时的视图；UCS 任务没有提前生成 DCS，必须在这里恢复扫描时的 UCS 视图。
        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

        var corners = CadSelectionWindow.GetJobWorldCorners(job);
        var center = new Point3d(
            corners.Average(point => point.X),
            corners.Average(point => point.Y),
            corners.Average(point => point.Z));
        var width = job.UsesUserCoordinateSystem
            ? Math.Max(Math.Abs(job.UcsMaxX - job.UcsMinX), 1)
            : Math.Max(Math.Abs(job.MaxX - job.MinX), 1);
        var height = job.UsesUserCoordinateSystem
            ? Math.Max(Math.Abs(job.UcsMaxY - job.UcsMinY), 1)
            : Math.Max(Math.Abs(job.MaxY - job.MinY), 1);

        using var view = doc.Editor.GetCurrentView();
        if (job.UsesUserCoordinateSystem)
        {
            var xAxis = new Vector3d(job.UcsXAxisX, job.UcsXAxisY, job.UcsXAxisZ).GetNormal();
            var yAxis = new Vector3d(job.UcsYAxisX, job.UcsYAxisY, job.UcsYAxisZ).GetNormal();
            view.ViewDirection = xAxis.CrossProduct(yAxis).GetNormal();
            view.ViewTwist = -Math.Atan2(xAxis.Y, xAxis.X);
        }
        else
        {
            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = 0;
        }

        view.Target = center;
        view.CenterPoint = Point2d.Origin;
        view.Width = width * 1.05;
        view.Height = height * 1.05;
        doc.Editor.SetCurrentView(view);
    }

    private static void RunPlot(PlotInfo plotInfo, string documentName, string outputPath, string sheetName)
    {
        using var engine = PlotFactory.CreatePublishEngine();
        using var progress = new PlotProgressDialog(false, 1, true);
        var plotStarted = false;
        var documentStarted = false;
        var sheetStarted = false;
        var pageStarted = false;
        var graphicsStarted = false;

        try
        {
            progress.set_PlotMsgString(PlotMessageIndex.DialogTitle, "批量打印");
            progress.set_PlotMsgString(PlotMessageIndex.CancelJobButtonMessage, "取消");
            progress.set_PlotMsgString(PlotMessageIndex.CancelSheetButtonMessage, "取消当前图纸");
            progress.set_PlotMsgString(PlotMessageIndex.SheetSetProgressCaption, "批量打印进度");
            progress.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, sheetName);
            progress.LowerPlotProgressRange = 0;
            progress.UpperPlotProgressRange = 100;
            progress.PlotProgressPos = 0;
            progress.OnBeginPlot();
            progress.IsVisible = true;

            engine.BeginPlot(progress, null);
            plotStarted = true;
            engine.BeginDocument(plotInfo, documentName, null, 1, true, outputPath);
            documentStarted = true;
            progress.OnBeginSheet();
            sheetStarted = true;

            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, plotInfo, true, null);
            pageStarted = true;
            engine.BeginGenerateGraphics(null);
            graphicsStarted = true;
            engine.EndGenerateGraphics(null);
            graphicsStarted = false;
            engine.EndPage(null);
            pageStarted = false;

            progress.OnEndSheet();
            sheetStarted = false;
            engine.EndDocument(null);
            documentStarted = false;
            progress.PlotProgressPos = 100;
            progress.OnEndPlot();
            engine.EndPlot(null);
            plotStarted = false;
        }
        finally
        {
            if (graphicsStarted) TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            if (pageStarted) TryPlotCleanup(() => engine.EndPage(null));
            if (sheetStarted) TryPlotCleanup(progress.OnEndSheet);
            if (documentStarted) TryPlotCleanup(() => engine.EndDocument(null));
            if (plotStarted)
            {
                TryPlotCleanup(progress.OnEndPlot);
                TryPlotCleanup(() => engine.EndPlot(null));
            }
        }
    }

    private static void PreviewDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document plotDocument)
    {
        WaitForPlotIdle();

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var singleSettings = AppSettingsStore.Load();
            var frameLayerApplied = singleSettings.HideFrameBoundaryWhenPlotting
                && TemporaryFramePlotLayer.Apply(tr, db, job);
            var layout = FindLayoutForJob(tr, db, job);
            using var plotSettings = new PlotSettings(layout.ModelType);
            plotSettings.CopyFrom(layout);
            var plotWindow = GetPlotWindow(job, plotDocument);

            var validator = PlotSettingsValidator.Current;
            var media = job.RequireExactPaperSize
                ? TrySelectExactSingleMediaWithoutRefresh(
                    validator, plotSettings, job, singleSettings, deviceName, layout.ModelType)
                : null;
            if (media == null && HasCachedMediaNames(deviceName, layout.ModelType))
            {
                // 已有同一 PC5/PMP 的纸张目录时，只绑定设备，不再卸载设备和刷新全部列表。
                validator.SetPlotConfigurationName(plotSettings, deviceName, null);
            }
            else if (media == null)
            {
                // 首次使用或配置文件变化后强制重新读取 PMP，随后缓存纸张目录。
                validator.SetPlotConfigurationName(plotSettings, "None", null);
                validator.SetPlotConfigurationName(plotSettings, deviceName, null);
                validator.RefreshLists(plotSettings);
            }
            TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);

            media ??= SelectMedia(validator, plotSettings, job, singleSettings, deviceName, layout.ModelType, plotWindow);
            if (media == null)
            {
                throw new InvalidOperationException($"未找到匹配 {job.PaperSizeText} 的打印纸张。");
            }

            validator.SetCanonicalMediaName(plotSettings, media.Name);
            EnsureExactMediaSize(plotSettings, job);
            if (!string.IsNullOrWhiteSpace(styleSheet))
            {
                validator.SetCurrentStyleSheet(plotSettings, styleSheet);
            }

            validator.SetPlotWindowArea(plotSettings, plotWindow);
            validator.SetPlotType(plotSettings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
            ConfigurePlotScale(validator, plotSettings, plotWindow, job);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media, job, plotWindow, deviceName));

            var plotInfo = new PlotInfo
            {
                Layout = layout.ObjectId,
                OverrideSettings = plotSettings
            };
            new PlotInfoValidator
            {
                MediaMatchingPolicy = MatchingPolicy.MatchEnabled
            }.Validate(plotInfo);

            RunPreview(plotInfo, documentName);

            // 预览必须和正式输出使用同一套外框可打印状态；应用临时移层后不提交事务，
            // 预览关闭、失败或取消时均由 CAD 原子回滚，避免修改用户图纸。
            if (!frameLayerApplied)
            {
                tr.Commit();
            }
            WaitForPlotIdle();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    private static void RunPreview(PlotInfo plotInfo, string documentName)
    {
        using var engine = PlotFactory.CreatePreviewEngine((int)PreviewEngineFlags.Plot);
        var plotStarted = false;
        var documentStarted = false;
        var pageStarted = false;
        var graphicsStarted = false;

        try
        {
            engine.BeginPlot(null, null);
            plotStarted = true;
            engine.BeginDocument(plotInfo, documentName, null, 1, false, null);
            documentStarted = true;
            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, plotInfo, true, null);
            pageStarted = true;
            engine.BeginGenerateGraphics(null);
            graphicsStarted = true;
            engine.EndGenerateGraphics(null);
            graphicsStarted = false;
            engine.EndPage(null);
            pageStarted = false;
            engine.EndDocument(null);
            documentStarted = false;
            engine.EndPlot(null);
            plotStarted = false;
        }
        finally
        {
            if (graphicsStarted) TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            if (pageStarted) TryPlotCleanup(() => engine.EndPage(null));
            if (documentStarted) TryPlotCleanup(() => engine.EndDocument(null));
            if (plotStarted) TryPlotCleanup(() => engine.EndPlot(null));
        }
    }

    private static void PrepareOutputFile(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("输出路径缺少目录: " + outputPath);
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    private static void ValidatePlotOutput(string outputPath)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new IOException("打印引擎未生成输出文件: " + outputPath);
        }

        if (string.Equals(Path.GetExtension(outputPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            using var stream = File.OpenRead(outputPath);
            var actual = new byte[signature.Length];
            if (stream.Read(actual, 0, actual.Length) != actual.Length || !actual.SequenceEqual(signature))
            {
                throw new InvalidDataException("PNG 已生成但文件格式无效，已按打印失败处理: " + outputPath);
            }
            return;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".jpg", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetExtension(outputPath), ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(outputPath);
            var signature = new byte[3];
            if (stream.Read(signature, 0, signature.Length) != signature.Length
                || signature[0] != 0xFF
                || signature[1] != 0xD8
                || signature[2] != 0xFF)
            {
                throw new InvalidDataException("JPG 已生成但文件格式无效，已按打印失败处理: " + outputPath);
            }
            return;
        }

        if (string.Equals(Path.GetExtension(outputPath), ".dwf", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(outputPath);
            var signature = new byte[6];
            if (stream.Read(signature, 0, signature.Length) != signature.Length
                || !string.Equals(System.Text.Encoding.ASCII.GetString(signature), "(DWF V", StringComparison.Ordinal))
            {
                throw new InvalidDataException("DWF 已生成但文件格式无效，已按打印失败处理: " + outputPath);
            }
            return;
        }

        using var pdf = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0)
        {
            throw new InvalidDataException("PDF 已生成但没有有效页面，已按打印失败处理: " + outputPath);
        }
    }

    private static void WaitForPlotIdle()
    {
        const int timeoutMs = 10 * 60 * 1000;
        var waited = 0;
        while (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            if (waited >= timeoutMs)
            {
                throw new InvalidOperationException("CAD 当前打印任务长时间未结束。");
            }

            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(250);
            waited += 250;
        }
    }

    private static void TryPlotCleanup(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    private static void TryCloseWithoutSave(Document doc)
    {
        try
        {
            doc.CloseAndDiscard();
        }
        catch
        {
            // Printing is already complete. Leave the document open rather than risk saving user data.
        }
    }

    private static MediaSelection? SelectMedia(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        bool modelType,
        Extents2d rasterWindow)
    {
        var media = GetMediaNames(validator, plotSettings, deviceName, modelType);
        var windowWidth = Math.Abs(rasterWindow.MaxPoint.X - rasterWindow.MinPoint.X);
        var windowHeight = Math.Abs(rasterWindow.MaxPoint.Y - rasterWindow.MinPoint.Y);
        return SelectMediaFromNames(media, job, settings, deviceName, windowWidth, windowHeight);
    }

    private static MediaSelection? SelectMediaFromNames(
        IReadOnlyList<string> media,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        double rasterWindowWidth = 0d,
        double rasterWindowHeight = 0d)
    {
        if (media.Count == 0)
        {
            return null;
        }

        if (IsRasterPlotDevice(deviceName))
        {
            RasterPlotOrientation.GetDcsOrientedPaperSize(
                job, rasterWindowWidth, rasterWindowHeight, out var rasterWidth, out var rasterHeight);
            return FindRasterMediaByAspectRatio(media, rasterWidth, rasterHeight);
        }

        var tolerance = job.RequireExactPaperSize
            ? ExactMediaToleranceMm
            : settings.PaperMatchToleranceMm;
        // 扩大纸张留白模式：按有效尺寸（含留白）选纸
        var searchWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var searchHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var exact = FindByPhysicalSize(media, searchWidth, searchHeight, tolerance);
        if (exact != null)
        {
            return exact;
        }

        if (job.RequireExactPaperSize)
            return null;

        // 名称兜底是标准纸张的固定兼容策略，不再作为用户设置；精确任意纸张已在上方直接返回，仍禁止兜底。
        var paperName = job.PaperName ?? "";
        var plusIndex = paperName.IndexOf('+');
        var basePaper = plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
        if (plusIndex > 0)
        {
            var longNamed = media.FirstOrDefault(x => x.IndexOf(paperName, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && x.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0);
            if (longNamed != null)
            {
                return new MediaSelection { Name = longNamed, NeedsRotation = false };
            }
        }
        else
        {
            var named = media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
            if (named != null)
            {
                return new MediaSelection { Name = named, NeedsRotation = false };
            }
        }

        return null;
    }

    private static MediaSelection? FindRasterMediaByAspectRatio(
        IEnumerable<string> mediaNames,
        double targetWidth,
        double targetHeight)
    {
        if (targetWidth <= 0d || targetHeight <= 0d)
        {
            return null;
        }

        var targetAspect = Math.Max(targetWidth, targetHeight) / Math.Min(targetWidth, targetHeight);
        // 原生 PNG/JPG 绘图器的介质通常以像素命名，并不等于 A 系列毫米纸张。
        // 按长宽比选择最接近且分辨率最大的介质，再由 ScaleToFit 保证图框完整输出。
        return mediaNames
            .Select(name => new { Name = name, Size = TryParseMediaSize(name) })
            .Where(item => item.Size != null
                           && item.Size.Value.Width > 0d
                           && item.Size.Value.Height > 0d)
            .Select(item => new
            {
                item.Name,
                Width = item.Size!.Value.Width,
                Height = item.Size.Value.Height,
                AspectError = Math.Abs(Math.Log(
                    (Math.Max(item.Size.Value.Width, item.Size.Value.Height)
                     / Math.Min(item.Size.Value.Width, item.Size.Value.Height)) / targetAspect))
            })
            .OrderBy(item => item.AspectError)
            // 同比例介质优先选择与 DCS 窗口相同的方向；只有设备缺少该方向时才旋转。
            .ThenBy(item => (item.Width >= item.Height) == (targetWidth >= targetHeight) ? 0 : 1)
            .ThenByDescending(item => item.Width * item.Height)
            .Select(item => new MediaSelection
            {
                Name = item.Name,
                NeedsRotation = (item.Width >= item.Height) != (targetWidth >= targetHeight)
            })
            .FirstOrDefault();
    }

    private static bool IsRasterPlotDevice(string deviceName)
    {
        return deviceName.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static MediaSelection? TrySelectExactSingleMediaWithoutRefresh(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        bool modelType)
    {
        // 先只重绑设备并读取当前列表；只有新 PMP 尚未可见时，调用方才回退到完整 RefreshLists。
        if (job.CustomPaperWasAdded)
            validator.SetPlotConfigurationName(plotSettings, "None", null);

        validator.SetPlotConfigurationName(plotSettings, deviceName, null);
        TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);
        var names = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        var media = SelectMediaFromNames(names, job, settings, deviceName);
        if (media != null)
            SetCachedMediaNames(deviceName, modelType, names);

        return media;
    }

    private static bool HasCachedMediaNames(string deviceName, bool modelType)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            return MediaNameCache.ContainsKey(cacheKey);
        }
    }

    private static void SetCachedMediaNames(string deviceName, bool modelType, IReadOnlyList<string> media)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            MediaNameCache[cacheKey] = media;
        }
    }

    private static IReadOnlyList<string> GetMediaNames(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        string deviceName,
        bool modelType)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            if (MediaNameCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var media = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        lock (MediaNameCacheLock)
        {
            // 仅缓存纸张名称，不缓存任何与当前事务绑定的 ZWCAD 对象。
            MediaNameCache[cacheKey] = media;
        }

        return media;
    }

    private static string BuildMediaNameCacheKey(string deviceName, bool modelType)
    {
        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var devicePath = string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(plottersDirectory, deviceName);
        var pmpPath = string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(
                plottersDirectory,
                "PMP Files",
                Path.GetFileNameWithoutExtension(deviceName) + ".pmp");
        return string.Join("|", deviceName, modelType ? "M" : "P", GetFileFingerprint(devicePath), GetFileFingerprint(pmpPath));
    }

    private static string GetFileFingerprint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch
        {
            return "unavailable";
        }
    }

    private static bool TrySetPlotPaperUnits(PlotSettingsValidator validator, PlotSettings plotSettings, PlotPaperUnit units)
    {
        try
        {
            validator.SetPlotPaperUnits(plotSettings, units);
            return true;
        }
        catch (ZwSoft.ZwCAD.Runtime.Exception ex) when (ex.ErrorStatus == ZwSoft.ZwCAD.Runtime.ErrorStatus.InvalidInput)
        {
            return false;
        }
    }

    private static MediaSelection? FindByPhysicalSize(IEnumerable<string> mediaNames, double widthMm, double heightMm, double toleranceMm)
    {
        if (widthMm <= 0 || heightMm <= 0)
        {
            return null;
        }

        var parsed = mediaNames
            .Select(name => new { Name = name, Size = TryParseMediaSize(name) })
            .Where(x => x.Size != null)
            .Select(x => new
            {
                x.Name,
                DirectError = DirectSizeError(x.Size!.Value.Width, x.Size.Value.Height, widthMm, heightMm),
                RotatedError = DirectSizeError(x.Size.Value.Width, x.Size.Value.Height, heightMm, widthMm)
            })
            .ToList();

        var direct = parsed
            .Where(x => x.DirectError <= toleranceMm)
            .OrderBy(x => x.DirectError)
            .Select(x => new MediaSelection { Name = x.Name, NeedsRotation = false })
            .FirstOrDefault();
        if (direct != null)
        {
            return direct;
        }

        return parsed
            .Where(x => x.RotatedError <= toleranceMm)
            .OrderBy(x => x.RotatedError)
            .Select(x => new MediaSelection { Name = x.Name, NeedsRotation = true })
            .FirstOrDefault();
    }

    private static (double Width, double Height)? TryParseMediaSize(string mediaName)
    {
        var match = Regex.Match(mediaName, @"(?<w>\d+(?:\.\d+)?)\s*[xX]\s*(?<h>\d+(?:\.\d+)?)\s*(?<unit>MM|毫米|IN|英寸)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit is "IN" or "英寸")
        {
            width *= 25.4;
            height *= 25.4;
        }

        return (width, height);
    }

    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

    private static void EnsureExactMediaSize(PlotSettings plotSettings, PlotJob job)
    {
        if (!job.RequireExactPaperSize)
            return;

        var size = plotSettings.PlotPaperSize;
        // 扩大纸张留白模式：校验实际加载尺寸应等于有效尺寸（原始+留白×2），而非原始尺寸。
        var expectedW = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var expectedH = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var direct = DirectSizeError(size.X, size.Y, expectedW, expectedH);
        var rotated = DirectSizeError(size.X, size.Y, expectedH, expectedW);
        if (Math.Min(direct, rotated) <= ExactMediaToleranceMm)
            return;

        throw new InvalidOperationException(
            $"中望 CAD 实际加载纸张 {size.X:0.######} x {size.Y:0.######} mm，"
            + $"与任意纸张 {expectedW:0.######} x {expectedH:0.######} mm 不一致；"
            + "已停止打印，禁止生成错误页幅。");
    }

    private static PlotRotation DetectRotation(
        MediaSelection? media,
        PlotJob job,
        Extents2d window,
        string deviceName)
    {
        var paperRotation = media?.NeedsRotation == true
            ? PlotRotation.Degrees090
            : PlotRotation.Degrees000;
        if (IsRasterPlotDevice(deviceName))
        {
            // 栅格目标方向来自 PlotWindowArea 使用的同一 DCS 窗口，禁止再用 WCS 或默认纸张方向翻转。
            return paperRotation;
        }

        // 扩大纸张模式下以有效尺寸（含留白）判断横竖方向，保证与实际纸张方向一致。
        var paperWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var paperHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        if (paperWidth <= 1e-9 || paperHeight <= 1e-9
            || windowWidth <= 1e-9 || windowHeight <= 1e-9)
        {
            return paperRotation;
        }

        var paperIsLandscape = paperWidth >= paperHeight;
        var windowIsLandscape = windowWidth >= windowHeight;
        if (paperIsLandscape == windowIsLandscape)
        {
            return paperRotation;
        }

        return ToggleQuarterTurn(paperRotation);
    }

    private static PlotRotation ToggleQuarterTurn(PlotRotation paperRotation)
    {
        return paperRotation == PlotRotation.Degrees090
            ? PlotRotation.Degrees000
            : PlotRotation.Degrees090;
    }
}
