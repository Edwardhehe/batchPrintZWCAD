using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PdfSharp.Pdf.IO;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public static class PlotterService
{
    private const double MediaMatchToleranceMm = 3d;

    public sealed class PlotJobResult
    {
        public PlotJob Job { get; set; } = new();
        public Exception? Error { get; set; }
        public bool Succeeded => Error == null;
    }

    private sealed class MediaChoice
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double Error { get; set; }
        public bool IsFullBleed { get; set; }
        public bool UseClosestBySize { get; set; }
        public bool RequiresExactSize { get; set; }
        public PlotRotation PreferredRotation { get; set; }
    }

    private sealed class ValidatedPlot : IDisposable
    {
        public PlotInfo Info { get; set; } = new();
        public PlotSettings Settings { get; set; } = null!;
        public MediaChoice Media { get; set; } = new();
        public PlotRotation Rotation { get; set; }

        public void Dispose()
        {
            Settings.Dispose();
        }
    }

    public static List<PlotJobResult> PlotMany(
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Document currentDocument,
        AppSettings settings,
        Action<PlotJob>? beforeJob = null)
    {
        var results = new List<PlotJobResult>();
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;

        using var variables = PlotSystemVariables.Apply();
        try
        {
            foreach (var group in jobs.GroupBy(job => GetGroupKey(job, currentDocument, settings)))
            {
                var groupJobs = group.ToList();
                try
                {
                    if (group.Key == "__CURRENT__")
                    {
                        PlotDocumentJobs(currentDocument, groupJobs, deviceName, styleSheet, beforeJob, results);
                    }
                    else if (group.Key.StartsWith("__DB__:", StringComparison.OrdinalIgnoreCase))
                    {
                        PlotSideDatabaseJobs(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, beforeJob, results);
                    }
                    else
                    {
                        PlotOpenedDocumentJobs(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, beforeJob, results);
                    }
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
        var result = PlotMany(new[] { job }, deviceName, styleSheet, currentDocument, settings).FirstOrDefault();
        if (result?.Error != null)
        {
            throw result.Error;
        }
    }

    public static void Preview(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;
        var doc = IsCurrentDocumentJob(job, currentDocument) ? currentDocument : FindOpenDocument(job.SourceFile);
        var shouldClose = doc == null;
        doc ??= OpenDocument(job.SourceFile);

        using var variables = PlotSystemVariables.Apply();
        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            using (doc.LockDocument())
            {
                RefreshJobsFromDatabase(doc.Database, new[] { job });
                ActivateLayout(doc.Database, job);
                PrepareEditorViewForPlot(doc, job);
                PreviewDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
            }
        }
        finally
        {
            if (shouldClose)
            {
                CloseWithoutSave(doc);
            }

            if (oldActive != null && !oldActive.IsDisposed)
            {
                CadApp.DocumentManager.MdiActiveDocument = oldActive;
            }
        }
    }

    private static string GetGroupKey(PlotJob job, Document currentDocument, AppSettings settings)
    {
        if (IsCurrentDocumentJob(job, currentDocument))
        {
            return "__CURRENT__";
        }

        var file = string.IsNullOrWhiteSpace(job.SourceFile) ? "" : Path.GetFullPath(job.SourceFile);
        return settings.OpenExternalDwgForPlot ? file : "__DB__:" + file;
    }

    private static void PlotOpenedDocumentJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results)
    {
        var doc = FindOpenDocument(sourceFile);
        var shouldClose = doc == null;
        doc ??= OpenDocument(sourceFile);

        try
        {
            PlotDocumentJobs(doc, jobs, deviceName, styleSheet, beforeJob, results);
        }
        finally
        {
            if (shouldClose)
            {
                CloseWithoutSave(doc);
            }
        }
    }

    private static void PlotDocumentJobs(
        Document doc,
        IReadOnlyList<PlotJob> jobs,
        string deviceName,
        string styleSheet,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results)
    {
        CadApp.DocumentManager.MdiActiveDocument = doc;

        using (doc.LockDocument())
        {
            RefreshJobsFromDatabase(doc.Database, jobs);
        }

        foreach (var job in jobs)
        {
            try
            {
                beforeJob?.Invoke(job);
                using (doc.LockDocument())
                {
                    ActivateLayout(doc.Database, job);
                    PrepareEditorViewForPlot(doc, job);
                    PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, doc);
                }

                results.Add(new PlotJobResult { Job = job });
            }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    private static void PlotSideDatabaseJobs(
        IReadOnlyList<PlotJob> jobs,
        string sourceFile,
        string deviceName,
        string styleSheet,
        Action<PlotJob>? beforeJob,
        List<PlotJobResult> results)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        db.ResolveXrefs(true, false);
        RefreshJobsFromDatabase(db, jobs);

        foreach (var job in jobs)
        {
            try
            {
                beforeJob?.Invoke(job);
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, null);
                results.Add(new PlotJobResult { Job = job });
            }
            catch (Exception ex)
            {
                results.Add(new PlotJobResult { Job = job, Error = ex });
            }
        }
    }

    private static void PlotDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document? plotDocument)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new InvalidOperationException("请选择 PDF 打印机。");
        }

        WaitForPlotIdle();

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(layout, job, window, deviceName, styleSheet);

            PrepareOutputFile(job.OutputPath);
            RunPlot(plot.Info, documentName, job.OutputPath, job.DrawingNumber);

            tr.Commit();
            WaitForPlotIdle();
            ValidatePdfOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    private static ValidatedPlot CreateValidatedPlot(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet)
    {
        var validator = PlotSettingsValidator.Current;
        var media = ChooseMedia(validator, layout, deviceName, job);
        var errors = new List<string>();

        var preferredRotation = ResolveWindowRotation(media.PreferredRotation, job, window);
        foreach (var rotation in RotationOrder(preferredRotation))
        {
            var settings = new PlotSettings(layout.ModelType);
            try
            {
                settings.CopyFrom(layout);
                ConfigurePlotSettings(validator, settings, deviceName, styleSheet, media, rotation, window);

                var info = new PlotInfo
                {
                    Layout = layout.ObjectId,
                    OverrideSettings = settings
                };

                new PlotInfoValidator
                {
                    MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                }.Validate(info);

                return new ValidatedPlot
                {
                    Info = info,
                    Settings = settings,
                    Media = media,
                    Rotation = rotation
                };
            }
            catch (Exception ex)
            {
                errors.Add($"{media.Name}/{rotation}: {ex.Message}");
                settings.Dispose();
            }
        }

        throw new InvalidOperationException(
            "AutoCAD 不接受当前打印设置。"
            + $" 图纸={job.DrawingNumber}_{job.Title};"
            + $" 目标纸张={job.PaperWidthMm:0.##}x{job.PaperHeightMm:0.##}mm;"
            + $" 窗口=({window.MinPoint.X:0.###},{window.MinPoint.Y:0.###})-({window.MaxPoint.X:0.###},{window.MaxPoint.Y:0.###});"
            + " 尝试结果=" + string.Join(" | ", errors));
    }

    private static void ConfigurePlotSettings(
        PlotSettingsValidator validator,
        PlotSettings settings,
        string deviceName,
        string styleSheet,
        MediaChoice media,
        PlotRotation rotation,
        Extents2d window)
    {
        try
        {
            validator.SetPlotConfigurationName(settings, deviceName, media.UseClosestBySize ? null : media.Name);
        }
        catch
        {
            validator.SetPlotConfigurationName(settings, deviceName, null);
        }

        validator.RefreshLists(settings);
        validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
        if (media.UseClosestBySize)
        {
            validator.SetClosestMediaName(settings, media.WidthMm, media.HeightMm, PlotPaperUnit.Millimeters, false);
        }
        else
        {
            validator.SetCanonicalMediaName(settings, media.Name);
        }

        EnsureRequiredMediaSize(settings, media);
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
        validator.SetPlotWindowArea(settings, window);
        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
        validator.SetPlotCentered(settings, true);
        validator.SetPlotRotation(settings, rotation);

        if (!string.IsNullOrWhiteSpace(styleSheet))
        {
            validator.SetCurrentStyleSheet(settings, styleSheet);
        }
    }

    private static MediaChoice ChooseMedia(PlotSettingsValidator validator, Layout layout, string deviceName, PlotJob job)
    {
        using var settings = new PlotSettings(layout.ModelType);
        settings.CopyFrom(layout);
        validator.SetPlotConfigurationName(settings, deviceName, null);
        validator.RefreshLists(settings);
        validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);

        var names = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException($"打印机没有可用纸张: {deviceName}");
        }

        var targetWidth = job.PaperWidthMm > 0 ? job.PaperWidthMm : Math.Abs(job.MaxX - job.MinX);
        var targetHeight = job.PaperHeightMm > 0 ? job.PaperHeightMm : Math.Abs(job.MaxY - job.MinY);
        var choices = new List<MediaChoice>();

        foreach (var name in names)
        {
            var size = GetMediaSize(validator, settings, name);
            if (size == null)
            {
                continue;
            }

            var directError = DirectSizeError(size.Value.Width, size.Value.Height, targetWidth, targetHeight);
            var rotatedError = DirectSizeError(size.Value.Width, size.Value.Height, targetHeight, targetWidth);
            choices.Add(new MediaChoice
            {
                Name = name,
                WidthMm = size.Value.Width,
                HeightMm = size.Value.Height,
                Error = Math.Min(directError, rotatedError),
                IsFullBleed = IsFullBleedMedia(name),
                PreferredRotation = rotatedError < directError ? PlotRotation.Degrees090 : PlotRotation.Degrees000
            });
        }

        var exact = choices
            .Where(x => x.Error <= MediaMatchToleranceMm)
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exact != null && IsLongPaperName(job.PaperName ?? ""))
        {
            exact.RequiresExactSize = true;
            return exact;
        }

        var named = BestNamedMedia(choices, job);
        if (named != null)
        {
            named.RequiresExactSize = IsLongPaperName(job.PaperName ?? "");
            return named;
        }

        if (exact != null)
        {
            return exact;
        }

        if (IsLongPaperName(job.PaperName ?? "") && targetWidth > 0 && targetHeight > 0)
        {
            return new MediaChoice
            {
                Name = $"按尺寸匹配 {targetWidth:0.##} x {targetHeight:0.##} mm",
                WidthMm = targetWidth,
                HeightMm = targetHeight,
                Error = 0,
                UseClosestBySize = true,
                RequiresExactSize = true,
                PreferredRotation = targetWidth >= targetHeight ? PlotRotation.Degrees090 : PlotRotation.Degrees000
            };
        }

        var closest = choices
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (closest != null)
        {
            return closest;
        }

        var fallbackName = BestMediaNameByText(names, job) ?? names[0];
        return new MediaChoice
        {
            Name = fallbackName,
            PreferredRotation = job.PaperWidthMm >= job.PaperHeightMm ? PlotRotation.Degrees090 : PlotRotation.Degrees000
        };
    }

    private static MediaChoice? BestNamedMedia(IEnumerable<MediaChoice> choices, PlotJob job)
    {
        var paper = job.PaperName ?? "";
        var basePaper = paper.Replace("+", "");
        return choices
            .Where(x => MediaNameMatchesPaper(x.Name, paper, basePaper))
            .OrderBy(x => x.Error)
            .ThenBy(x => x.IsFullBleed ? 0 : 1)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void EnsureRequiredMediaSize(PlotSettings settings, MediaChoice media)
    {
        if (!media.RequiresExactSize)
        {
            return;
        }

        var size = settings.PlotPaperSize;
        if (size.X <= 0 || size.Y <= 0)
        {
            return;
        }

        var directError = DirectSizeError(size.X, size.Y, media.WidthMm, media.HeightMm);
        var rotatedError = DirectSizeError(size.X, size.Y, media.HeightMm, media.WidthMm);
        var error = Math.Min(directError, rotatedError);
        if (error <= MediaMatchToleranceMm)
        {
            return;
        }

        throw new InvalidOperationException(
            $"AutoCAD PDF 打印机缺少匹配纸张。需要 {media.WidthMm:0.##} x {media.HeightMm:0.##} mm，"
            + $"实际匹配到 {size.X:0.##} x {size.Y:0.##} mm。请在所选 PC3 中添加对应加长纸，或选择支持自定义纸张的 PDF 打印机。");
    }

    private static string? BestMediaNameByText(IEnumerable<string> names, PlotJob job)
    {
        var paper = job.PaperName ?? "";
        var basePaper = paper.Replace("+", "");
        return names
            .Where(x => MediaNameMatchesPaper(x, paper, basePaper))
            .OrderBy(x => IsFullBleedMedia(x) ? 0 : 1)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool MediaNameMatchesPaper(string mediaName, string paper, string basePaper)
    {
        if (IsLongPaperName(paper))
        {
            return (!string.IsNullOrWhiteSpace(paper)
                    && mediaName.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
                || (!string.IsNullOrWhiteSpace(basePaper)
                    && mediaName.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && IsLongMediaName(mediaName))
                || (!string.IsNullOrWhiteSpace(basePaper)
                    && mediaName.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0
                    && IsLongMediaName(mediaName));
        }

        return (!string.IsNullOrWhiteSpace(paper)
                && mediaName.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(basePaper)
                && mediaName.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
            || (!string.IsNullOrWhiteSpace(basePaper)
                && mediaName.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static bool IsLongPaperName(string paperName)
    {
        return paperName.EndsWith("+", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLongMediaName(string mediaName)
    {
        return mediaName.IndexOf("+", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("long", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extend", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("extended", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsFullBleedMedia(string mediaName)
    {
        return mediaName.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("full bleed", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("无边距", StringComparison.OrdinalIgnoreCase) >= 0
            || mediaName.IndexOf("满幅", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static IEnumerable<PlotRotation> RotationOrder(PlotRotation preferred)
    {
        yield return preferred;

        foreach (var rotation in new[]
        {
            PlotRotation.Degrees000,
            PlotRotation.Degrees090,
            PlotRotation.Degrees270,
            PlotRotation.Degrees180
        })
        {
            if (rotation != preferred)
            {
                yield return rotation;
            }
        }
    }

    private static (double Width, double Height)? GetMediaSize(PlotSettingsValidator validator, PlotSettings settings, string mediaName)
    {
        try
        {
            validator.SetCanonicalMediaName(settings, mediaName);
            validator.SetPlotPaperUnits(settings, PlotPaperUnit.Millimeters);
            var size = settings.PlotPaperSize;
            if (size.X > 0 && size.Y > 0)
            {
                return (size.X, size.Y);
            }
        }
        catch
        {
        }

        return TryParseMediaSize(mediaName);
    }

    private static void RunPlot(PlotInfo info, string documentName, string outputPath, string sheetName)
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
            progress.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, sheetName);
            progress.LowerPlotProgressRange = 0;
            progress.UpperPlotProgressRange = 100;
            progress.PlotProgressPos = 0;
            progress.OnBeginPlot();
            progress.IsVisible = true;

            engine.BeginPlot(progress, null);
            plotStarted = true;
            engine.BeginDocument(info, documentName, null, 1, true, outputPath);
            documentStarted = true;
            progress.OnBeginSheet();
            sheetStarted = true;

            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, info, true, null);
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
            if (graphicsStarted)
            {
                TryPlotCleanup(() => engine.EndGenerateGraphics(null));
            }

            if (pageStarted)
            {
                TryPlotCleanup(() => engine.EndPage(null));
            }

            if (sheetStarted)
            {
                TryPlotCleanup(progress.OnEndSheet);
            }

            if (documentStarted)
            {
                TryPlotCleanup(() => engine.EndDocument(null));
            }

            if (plotStarted)
            {
                TryPlotCleanup(progress.OnEndPlot);
                TryPlotCleanup(() => engine.EndPlot(null));
            }
        }
    }

    private static Layout FindLayoutForJob(Transaction tr, Database db, PlotJob job)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Layout? first = null;
        Layout? model = null;
        Layout? firstPaper = null;
        var availableLayouts = new List<string>();

        foreach (ObjectId id in blockTable)
        {
            var record = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!record.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(record.LayoutId, OpenMode.ForRead);
            availableLayouts.Add(layout.LayoutName);
            first ??= layout;
            if (layout.ModelType)
            {
                model ??= layout;
            }
            else
            {
                firstPaper ??= layout;
            }

            if (string.Equals(record.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(layout.LayoutName, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        if (!string.IsNullOrWhiteSpace(job.SpaceName))
        {
            throw new InvalidOperationException(
                $"未找到目标布局“{job.SpaceName}”。可用布局: {string.Join(", ", availableLayouts)}。请重新扫描图纸。");
        }

        if (!job.IsPaperSpace && model != null)
        {
            return model;
        }

        if (job.IsPaperSpace && firstPaper != null)
        {
            return firstPaper;
        }

        return first ?? throw new InvalidOperationException("未找到可打印布局。");
    }

    private static void ActivateLayout(Database db, PlotJob job)
    {
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            LayoutManager.Current.CurrentLayout = layout.LayoutName;
            tr.Commit();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法激活目标布局“{job.SpaceName}”，已停止打印以避免输出错误区域。", ex);
        }
    }

    private static void RefreshJobsFromDatabase(Database db, IReadOnlyList<PlotJob> jobs)
    {
        var refreshableJobs = jobs.Where(job => !job.IsManualWindow).ToList();
        if (refreshableJobs.Count == 0)
        {
            return;
        }

        try
        {
            var library = TitleBlockLibraryStore.Load();
            var scanned = TitleBlockScanner.Scan(db, library, refreshableJobs[0].SourceFile);

            foreach (var job in refreshableJobs)
            {
                var refreshed = scanned
                    .Where(x => string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault(x =>
                        !string.IsNullOrWhiteSpace(job.BlockHandle)
                        && string.Equals(x.BlockHandle, job.BlockHandle, StringComparison.OrdinalIgnoreCase))
                    ?? scanned.FirstOrDefault(x =>
                        string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase)
                        &&
                        string.Equals(x.DrawingNumber, job.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.Title, job.Title, StringComparison.OrdinalIgnoreCase))
                    ?? scanned.FirstOrDefault(x =>
                        string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase)
                        && x.MatchIndex == job.MatchIndex);

                if (refreshed == null)
                {
                    throw new InvalidOperationException(
                        $"重新打开图纸后未找到原图框。布局={job.SpaceName}，块={job.BlockName}，句柄={job.BlockHandle}。请重新扫描图纸后再打印。");
                }

                job.MinX = refreshed.MinX;
                job.MinY = refreshed.MinY;
                job.MaxX = refreshed.MaxX;
                job.MaxY = refreshed.MaxY;
                job.IsDcsWindow = false;  // 重新扫描后坐标回到 WCS
                job.PaperName = refreshed.PaperName;
                job.PaperWidthMm = refreshed.PaperWidthMm;
                job.PaperHeightMm = refreshed.PaperHeightMm;
                job.PaperSizeText = refreshed.PaperSizeText;
                job.ScaleText = refreshed.ScaleText;
                job.SizeText = refreshed.SizeText;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("重新扫描已打开图纸失败，已停止打印以避免输出错误窗口。", ex);
        }
    }

    private static PlotRotation ResolveWindowRotation(
        PlotRotation paperRotation,
        PlotJob job,
        Extents2d window)
    {
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

        return paperRotation switch
        {
            PlotRotation.Degrees000 => PlotRotation.Degrees090,
            PlotRotation.Degrees090 => PlotRotation.Degrees000,
            PlotRotation.Degrees180 => PlotRotation.Degrees270,
            PlotRotation.Degrees270 => PlotRotation.Degrees180,
            _ => paperRotation
        };
    }

    private static void PreviewDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document plotDocument)
    {
        WaitForPlotIdle();

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(layout, job, window, deviceName, styleSheet);
            RunPreview(plot.Info, documentName);
            tr.Commit();
            WaitForPlotIdle();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    private static void PrepareEditorViewForPlot(Document doc, PlotJob job)
    {
        // 图纸空间无视图概念，跳过
        // IsDcsWindow：DCS 基于用户原始旋转视图计算，重置视图会破坏坐标系一致性
        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

        try
        {
            var minX = Math.Min(job.MinX, job.MaxX);
            var minY = Math.Min(job.MinY, job.MaxY);
            var maxX = Math.Max(job.MinX, job.MaxX);
            var maxY = Math.Max(job.MinY, job.MaxY);
            var width = Math.Max(maxX - minX, 1);
            var height = Math.Max(maxY - minY, 1);
            var centerX = (minX + maxX) / 2d;
            var centerY = (minY + maxY) / 2d;

            using var view = doc.Editor.GetCurrentView();
            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = 0;
            view.Target = new Point3d(centerX, centerY, 0);
            view.CenterPoint = Point2d.Origin;
            view.Width = width * 1.05;
            view.Height = height * 1.05;
            doc.Editor.SetCurrentView(view);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("无法规范打印视图，已停止打印以避免输出空白或偏移页面。", ex);
        }
    }

    private static Extents2d GetPlotWindow(PlotJob job, Document? plotDocument)
    {
        // 单张打印已在 BatchPlotCommands.SinglePlotCore 完成 UCS→DCS 全链路变换，直接使用
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
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    private static Document OpenDocument(string file)
    {
#if ACAD_CORE
        CadApp.DocumentManager.AppContextOpenDocument(file);
        return FindOpenDocument(file)
            ?? CadApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("AutoCAD 已打开 DWG，但插件未能取得文档对象: " + file);
#else
        return CadApp.DocumentManager.Open(file, false);
#endif
    }

    private static void CloseWithoutSave(Document doc)
    {
#if ACAD_CORE
        var close = doc.GetType().GetMethod("CloseAndDiscard");
        if (close == null)
        {
            return;
        }

        var parameters = close.GetParameters();
        if (parameters.Length == 0)
        {
            close.Invoke(doc, null);
        }
        else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
        {
            close.Invoke(doc, new object?[] { null });
        }
#else
        doc.CloseAndDiscard();
#endif
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
            var name = doc.Database.Filename;
            if (!string.IsNullOrWhiteSpace(name)
                && string.Equals(Path.GetFullPath(name), fullPath, StringComparison.OrdinalIgnoreCase))
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

        return !string.IsNullOrWhiteSpace(job.SourceFile)
            && string.Equals(Path.GetFullPath(job.SourceFile), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase);
    }

    private static void WaitForPlotIdle()
    {
        const int timeoutMs = 10 * 60 * 1000;
        var waited = 0;

        while (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            if (waited >= timeoutMs)
            {
                throw new InvalidOperationException("AutoCAD 当前打印任务长时间未结束。");
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

    private static void ValidatePdfOutput(string outputPath)
    {
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new IOException("打印引擎未生成 PDF 文件: " + outputPath);
        }

        using var pdf = PdfReader.Open(outputPath, PdfDocumentOpenMode.Import);
        if (pdf.PageCount == 0 || !pdf.Pages.Cast<PdfSharp.Pdf.PdfPage>().Any(page => page.Contents.Elements.Count > 0))
        {
            throw new InvalidDataException("PDF 已生成但页面内容为空，已按打印失败处理: " + outputPath);
        }
    }

    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

    private static (double Width, double Height)? TryParseMediaSize(string name)
    {
        var match = Regex.Match(
            name,
            @"(?<w>\d+(?:\.\d+)?)\s*[_-]?\s*(?:x|X|\u00D7)\s*[_-]?\s*(?<h>\d+(?:\.\d+)?)\s*[_-]?\s*(?<unit>MM|MILLIMETERS?|IN|INCH(?:ES)?)?",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit is "IN" or "INCH" or "INCHES")
        {
            width *= 25.4;
            height *= 25.4;
        }

        return (width, height);
    }

    private sealed class PlotSystemVariables : IDisposable
    {
        private readonly List<(string Name, object? Value)> _oldValues = new();
        private bool _disposed;

        public static PlotSystemVariables Apply()
        {
            var variables = new PlotSystemVariables();
            variables.Set("BACKGROUNDPLOT", 0);
            variables.Set("PUBLISHCOLLATE", 0);
            variables.Set("PDFSHX", 0);
            return variables;
        }

        private void Set(string name, object value)
        {
            try
            {
                var oldValue = CadApp.GetSystemVariable(name);
                CadApp.SetSystemVariable(name, value);
                _oldValues.Add((name, oldValue));
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (var i = _oldValues.Count - 1; i >= 0; i--)
            {
                try
                {
                    CadApp.SetSystemVariable(_oldValues[i].Name, _oldValues[i].Value);
                }
                catch
                {
                }
            }
        }
    }
}
