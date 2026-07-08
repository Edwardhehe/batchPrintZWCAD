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
                RefreshJobWindowFromOpenedDocument(doc.Database, job);
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
            var layout = FindLayoutForJob(tr, db, job);
            using var plotSettings = new PlotSettings(layout.ModelType);
            plotSettings.CopyFrom(layout);

            var validator = PlotSettingsValidator.Current;
            // 先卸再装，强制 ZWCAD 重新读 PMP 获取自定义纸张
            validator.SetPlotConfigurationName(plotSettings, "None", null);
            validator.SetPlotConfigurationName(plotSettings, deviceName, null);
            validator.RefreshLists(plotSettings);
            TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);

            var media = SelectMedia(validator, plotSettings, job, settings);
            if (media == null)
            {
                var allMedia = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
                var debugInfo = string.Join("|", allMedia.Where(x => x.IndexOf("Custom", StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf("UserDefined", StringComparison.OrdinalIgnoreCase) >= 0));
                throw new InvalidOperationException(
                    $"未找到匹配 {job.PaperSizeText} 的 PDF 纸张（{job.PaperWidthMm:0.##}x{job.PaperHeightMm:0.##}mm, name={job.PaperName}）。自定义纸张列表: {debugInfo}");
            }

            validator.SetCanonicalMediaName(plotSettings, media.Name);
            if (!string.IsNullOrWhiteSpace(styleSheet))
            {
                validator.SetCurrentStyleSheet(plotSettings, styleSheet);
            }

            var plotWindow = ApplyLeaveMargin(GetPlotWindow(job, plotDocument), job);
            validator.SetPlotWindowArea(plotSettings, plotWindow);
            validator.SetPlotType(plotSettings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
            validator.SetUseStandardScale(plotSettings, true);
            validator.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media, job, plotWindow));

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

            tr.Commit();
            WaitForPlotIdle();
            ValidatePdfOutput(job.OutputPath);
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

    private static Extents2d ApplyLeaveMargin(Extents2d window, PlotJob job)
    {
        if (!job.LeavePaperMargin)
        {
            return window;
        }

        var shortSide = Math.Min(job.PaperWidthMm, job.PaperHeightMm);
        if (shortSide <= 6d)
        {
            return window;
        }

        var scale = (shortSide - 6d) / shortSide;
        var centerX = (window.MinPoint.X + window.MaxPoint.X) / 2d;
        var centerY = (window.MinPoint.Y + window.MaxPoint.Y) / 2d;
        var halfWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X) / scale / 2d;
        var halfHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y) / scale / 2d;
        // 继续使用 ScaleToFit + 居中；放大打印窗口，让原图框在纸上等比例缩小并形成留白。
        return new Extents2d(centerX - halfWidth, centerY - halfHeight, centerX + halfWidth, centerY + halfHeight);
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
                var points = new[]
                {
                    new Point3d(job.MinX, job.MinY, 0).TransformBy(worldToDisplay),
                    new Point3d(job.MinX, job.MaxY, 0).TransformBy(worldToDisplay),
                    new Point3d(job.MaxX, job.MinY, 0).TransformBy(worldToDisplay),
                    new Point3d(job.MaxX, job.MaxY, 0).TransformBy(worldToDisplay)
                };

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
        // 图纸空间无视图概念，跳过
        // IsDcsWindow：单张打印 DCS 是基于用户原始旋转视图计算的，
        //   强制重置 ViewTwist=0 ViewDirection=ZAxis 会破坏 DCS 坐标系一致性
        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

        var minX = Math.Min(job.MinX, job.MaxX);
        var minY = Math.Min(job.MinY, job.MaxY);
        var maxX = Math.Max(job.MinX, job.MaxX);
        var maxY = Math.Max(job.MinY, job.MaxY);
        var width = Math.Max(maxX - minX, 1);
        var height = Math.Max(maxY - minY, 1);

        using var view = doc.Editor.GetCurrentView();
        view.ViewDirection = Vector3d.ZAxis;
        view.ViewTwist = 0;
        view.Target = new Point3d((minX + maxX) / 2d, (minY + maxY) / 2d, 0);
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
            var layout = FindLayoutForJob(tr, db, job);
            using var plotSettings = new PlotSettings(layout.ModelType);
            plotSettings.CopyFrom(layout);

            var validator = PlotSettingsValidator.Current;
            validator.SetPlotConfigurationName(plotSettings, "None", null);
            validator.SetPlotConfigurationName(plotSettings, deviceName, null);
            validator.RefreshLists(plotSettings);
            TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);

            var media = SelectMedia(validator, plotSettings, job, AppSettingsStore.Load());
            if (media == null)
            {
                throw new InvalidOperationException($"未找到匹配 {job.PaperSizeText} 的打印纸张。");
            }

            validator.SetCanonicalMediaName(plotSettings, media.Name);
            if (!string.IsNullOrWhiteSpace(styleSheet))
            {
                validator.SetCurrentStyleSheet(plotSettings, styleSheet);
            }

            var plotWindow = ApplyLeaveMargin(GetPlotWindow(job, plotDocument), job);
            validator.SetPlotWindowArea(plotSettings, plotWindow);
            validator.SetPlotType(plotSettings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
            validator.SetUseStandardScale(plotSettings, true);
            validator.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media, job, plotWindow));

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
            tr.Commit();
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
            throw new InvalidOperationException("PDF 输出路径缺少目录: " + outputPath);
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    private static void ValidatePdfOutput(string outputPath)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new IOException("打印引擎未生成 PDF 文件: " + outputPath);
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

    private static MediaSelection? SelectMedia(PlotSettingsValidator validator, PlotSettings plotSettings, PlotJob job, AppSettings settings)
    {
        var media = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        if (media.Count == 0)
        {
            return null;
        }

        var exact = FindByPhysicalSize(media, job.PaperWidthMm, job.PaperHeightMm, settings.PaperMatchToleranceMm);
        if (exact != null)
        {
            return exact;
        }

        if (!settings.AllowStandardPaperNameFallback)
        {
            return null;
        }

        var paperName = job.PaperName ?? "";
        var basePaper = paperName.Replace("+", "");
        if (paperName.EndsWith("+", StringComparison.OrdinalIgnoreCase))
        {
            var longNamed = media.FirstOrDefault(x => x.IndexOf(paperName, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && x.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0);
            return longNamed == null ? null : new MediaSelection { Name = longNamed, NeedsRotation = false };
        }

        var named = media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
            ?? media.FirstOrDefault(x => x.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
        return named == null ? null : new MediaSelection { Name = named, NeedsRotation = false };
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

    private static PlotRotation DetectRotation(MediaSelection? media, PlotJob job, Extents2d window)
    {
        var paperRotation = media?.NeedsRotation == true
            ? PlotRotation.Degrees090
            : PlotRotation.Degrees000;
        var paperWidth = job.PaperWidthMm;
        var paperHeight = job.PaperHeightMm;
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

        return paperRotation == PlotRotation.Degrees090
            ? PlotRotation.Degrees000
            : PlotRotation.Degrees090;
    }
}
