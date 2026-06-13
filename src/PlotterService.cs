using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
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
        Action<PlotJob>? beforeJob = null)
    {
        var results = new List<PlotJobResult>();
        var oldActive = CadApp.DocumentManager.MdiActiveDocument;

        foreach (var group in jobs.GroupBy(job => GetPlotGroupKey(job, currentDocument, settings)))
        {
            var groupJobs = group.ToList();
            try
            {
                if (group.Key == "__CURRENT__")
                {
                    PlotCurrentDocumentGroup(groupJobs, currentDocument, deviceName, styleSheet, settings, beforeJob, results);
                    continue;
                }

                if (group.Key.StartsWith("__DB__:", StringComparison.OrdinalIgnoreCase))
                {
                    PlotSideDatabaseGroup(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, settings, beforeJob, results);
                    continue;
                }

                PlotOpenedDocumentGroup(groupJobs, groupJobs[0].SourceFile, deviceName, styleSheet, settings, beforeJob, results);
            }
            catch (Exception ex)
            {
                foreach (var job in groupJobs)
                {
                    results.Add(new PlotJobResult { Job = job, Error = ex });
                }
            }
        }

        if (oldActive != null && !oldActive.IsDisposed)
        {
            CadApp.DocumentManager.MdiActiveDocument = oldActive;
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
        List<PlotJobResult> results)
    {
        CadApp.DocumentManager.MdiActiveDocument = currentDocument;
        foreach (var job in jobs)
        {
            try
            {
                beforeJob?.Invoke(job);
                using (currentDocument.LockDocument())
                {
                    ActivateLayout(job);
                    RefreshJobWindowFromOpenedDocument(currentDocument.Database, job);
                    PlotDatabase(currentDocument.Database, currentDocument.Name, job, deviceName, styleSheet, settings, currentDocument);
                }

                results.Add(new PlotJobResult { Job = job });
            }
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
        List<PlotJobResult> results)
    {
        var doc = FindOpenDocument(sourceFile);
        var shouldClose = doc == null;
        doc ??= CadApp.DocumentManager.Open(sourceFile, false);

        try
        {
            CadApp.DocumentManager.MdiActiveDocument = doc;
            foreach (var job in jobs)
            {
                try
                {
                    beforeJob?.Invoke(job);
                    using (doc.LockDocument())
                    {
                        ActivateLayout(job);
                        RefreshJobWindowFromOpenedDocument(doc.Database, job);
                        PlotDatabase(doc.Database, doc.Name, job, deviceName, styleSheet, settings, doc);
                    }

                    results.Add(new PlotJobResult { Job = job });
                }
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
                doc.CloseAndDiscard();
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
        List<PlotJobResult> results)
    {
        using var db = new Database(false, true);
        db.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        db.ResolveXrefs(true, false);

        foreach (var job in jobs)
        {
            try
            {
                beforeJob?.Invoke(job);
                PlotDatabase(db, Path.GetFileName(sourceFile), job, deviceName, styleSheet, settings);
                results.Add(new PlotJobResult { Job = job });
            }
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
                doc.CloseAndDiscard();
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
        catch
        {
            // Some drawings expose model space/layout names differently; PlotDatabase still targets the layout by name.
        }
    }

    private static void RefreshJobWindowFromOpenedDocument(Database db, PlotJob job)
    {
        var library = TitleBlockLibraryStore.Load();
        var candidates = TitleBlockScanner.Scan(db, library, job.SourceFile)
            .Where(x => string.Equals(x.SpaceName, job.SpaceName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.BlockName, job.BlockName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var refreshed = candidates.FirstOrDefault(x =>
                string.Equals(x.DrawingNumber, job.DrawingNumber, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Title, job.Title, StringComparison.OrdinalIgnoreCase))
            ?? candidates.ElementAtOrDefault(job.MatchIndex)
            ?? candidates.FirstOrDefault();

        if (refreshed == null)
        {
            return;
        }

        job.MinX = refreshed.MinX;
        job.MinY = refreshed.MinY;
        job.MaxX = refreshed.MaxX;
        job.MaxY = refreshed.MaxY;
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
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            throw new InvalidOperationException("CAD 当前正在打印，请稍后再试。");
        }

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            using var plotSettings = new PlotSettings(layout.ModelType);
            plotSettings.CopyFrom(layout);

            var validator = PlotSettingsValidator.Current;
            validator.SetPlotConfigurationName(plotSettings, deviceName, null);
            validator.SetPlotPaperUnits(plotSettings, PlotPaperUnit.Millimeters);
            validator.RefreshLists(plotSettings);

            var media = SelectMedia(validator, plotSettings, job, settings);
            if (media == null)
            {
                throw new InvalidOperationException(
                    $"未找到匹配 {job.PaperSizeText} 的 PDF 纸张。请在中望 PDF 打印机中添加这个自定义纸张后再打印。");
            }

            validator.SetCanonicalMediaName(plotSettings, media.Name);
            if (!string.IsNullOrWhiteSpace(styleSheet))
            {
                validator.SetCurrentStyleSheet(plotSettings, styleSheet);
            }

            var plotWindow = GetPlotWindow(job, plotDocument);
            validator.SetPlotWindowArea(plotSettings, plotWindow);
            validator.SetPlotType(plotSettings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
            validator.SetUseStandardScale(plotSettings, true);
            validator.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media));

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

            Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);

            using var engine = PlotFactory.CreatePublishEngine();
            using var progress = new PlotProgressDialog(false, 1, true);
            progress.set_PlotMsgString(PlotMessageIndex.DialogTitle, "批量打印");
            progress.set_PlotMsgString(PlotMessageIndex.CancelJobButtonMessage, "取消");
            progress.set_PlotMsgString(PlotMessageIndex.CancelSheetButtonMessage, "取消当前图纸");
            progress.set_PlotMsgString(PlotMessageIndex.SheetSetProgressCaption, "批量打印进度");
            progress.set_PlotMsgString(PlotMessageIndex.SheetProgressCaption, job.DrawingNumber);
            progress.LowerPlotProgressRange = 0;
            progress.UpperPlotProgressRange = 100;
            progress.PlotProgressPos = 0;
            progress.OnBeginPlot();
            progress.IsVisible = true;

            engine.BeginPlot(progress, null);
            engine.BeginDocument(plotInfo, documentName, null, 1, true, job.OutputPath);
            progress.OnBeginSheet();
            progress.LowerSheetProgressRange = 0;
            progress.UpperSheetProgressRange = 100;
            progress.SheetProgressPos = 0;

            using var pageInfo = new PlotPageInfo();
            engine.BeginPage(pageInfo, plotInfo, true, null);
            engine.BeginGenerateGraphics(null);
            engine.EndGenerateGraphics(null);
            engine.EndPage(null);

            progress.SheetProgressPos = 100;
            progress.OnEndSheet();
            engine.EndDocument(null);
            progress.PlotProgressPos = 100;
            progress.OnEndPlot();
            engine.EndPlot(null);

            tr.Commit();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    private static Layout FindLayoutForJob(Transaction tr, Database db, PlotJob job)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        Layout? firstLayout = null;
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout)
            {
                continue;
            }

            var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
            firstLayout ??= layout;
            if (string.Equals(owner.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                return layout;
            }
        }

        if (firstLayout != null)
        {
            return firstLayout;
        }

        throw new InvalidOperationException("未找到可打印布局。");
    }

    private static Extents2d GetPlotWindow(PlotJob job, Document? plotDocument)
    {
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

    private static PlotRotation DetectRotation(MediaSelection? media)
    {
        return media?.NeedsRotation == true ? PlotRotation.Degrees090 : PlotRotation.Degrees000;
    }
}
