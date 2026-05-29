using System;
using System.IO;
using System.Linq;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

namespace ZwcadBatchPlot;

public static class PlotterService
{
    public static void Plot(PlotJob job, string deviceName, string styleSheet, Document currentDocument)
    {
        var doc = GetDocumentForJob(job, currentDocument, out var shouldClose);
        try
        {
            using (doc.LockDocument())
            {
                PlotInDocument(doc, job, deviceName, styleSheet);
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

    private static Document GetDocumentForJob(PlotJob job, Document currentDocument, out bool shouldClose)
    {
        var currentFile = currentDocument.Database.Filename;
        if (string.Equals(Path.GetFullPath(job.SourceFile), Path.GetFullPath(currentFile), StringComparison.OrdinalIgnoreCase))
        {
            shouldClose = false;
            return currentDocument;
        }

        shouldClose = true;
        return CadApp.DocumentManager.Open(job.SourceFile, false);
    }

    private static void PlotInDocument(Document doc, PlotJob job, string deviceName, string styleSheet)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
        {
            throw new InvalidOperationException("CAD 当前正在打印，请稍后再试。");
        }

        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var layout = FindLayoutForJob(tr, db, job);
        using var plotSettings = new PlotSettings(layout.ModelType);
        plotSettings.CopyFrom(layout);

        var validator = PlotSettingsValidator.Current;
        validator.SetPlotConfigurationName(plotSettings, deviceName, null);
        validator.RefreshLists(plotSettings);
        var mediaName = SelectMediaName(validator, plotSettings, job.PaperName);
        if (!string.IsNullOrWhiteSpace(mediaName))
        {
            validator.SetCanonicalMediaName(plotSettings, mediaName);
        }

        if (!string.IsNullOrWhiteSpace(styleSheet))
        {
            validator.SetCurrentStyleSheet(plotSettings, styleSheet);
        }

        validator.SetPlotWindowArea(plotSettings, new Extents2d(job.MinX, job.MinY, job.MaxX, job.MaxY));
        validator.SetPlotType(plotSettings, ZwSoft.ZwCAD.DatabaseServices.PlotType.Window);
        validator.SetUseStandardScale(plotSettings, true);
        validator.SetStdScaleType(plotSettings, StdScaleType.ScaleToFit);
        validator.SetPlotCentered(plotSettings, true);
        validator.SetPlotRotation(plotSettings, DetectRotation(job));
        validator.SetPlotPaperUnits(plotSettings, PlotPaperUnit.Millimeters);

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
        engine.BeginDocument(plotInfo, doc.Name, null, 1, true, job.OutputPath);
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

    private static Layout FindLayoutForJob(Transaction tr, Database db, PlotJob job)
    {
        var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId recordId in blockTable)
        {
            var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
            if (!owner.IsLayout || !string.Equals(owner.Name, job.SpaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
        }

        return (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout), OpenMode.ForRead);
    }

    private static string? SelectMediaName(PlotSettingsValidator validator, PlotSettings settings, string paperName)
    {
        var basePaper = (paperName ?? "").Replace("+", "");
        if (string.IsNullOrWhiteSpace(basePaper))
        {
            return null;
        }

        var media = validator.GetCanonicalMediaNameList(settings).Cast<string>().ToList();
        return media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
            ?? media.FirstOrDefault(x => x.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0)
            ?? media.FirstOrDefault();
    }

    private static PlotRotation DetectRotation(PlotJob job)
    {
        var width = Math.Abs(job.MaxX - job.MinX);
        var height = Math.Abs(job.MaxY - job.MinY);
        return width >= height ? PlotRotation.Degrees090 : PlotRotation.Degrees000;
    }
}
