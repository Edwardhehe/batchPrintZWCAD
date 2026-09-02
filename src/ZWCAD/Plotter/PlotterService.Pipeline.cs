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

/**
 * @file PlotterService.Pipeline.cs（ZWCAD）
 * @description 出图主流程：配置 PlotSettings、校验、执行 Plot/Preview。
 *
 * 主要功能：
 * - PlotDatabase：单任务完整出图（设备、介质、窗口、比例、旋转）
 * - FindLayoutForJob：在事务中定位布局
 * - RunPlot / PreviewDatabase / RunPreview：PlotEngine 执行与预览
 *
 * 核心代码：
 * - plotSettings.CopyFrom(layout)：以布局页面设置为底再改设备
 * - SelectMedia + ConfigurePlotScale + GetPlotWindow：串起介质/比例/窗口
 *
 * 注意：与 AutoCAD Pipeline 职责对齐；平台 API 差异留在本文件内处理。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** PlotDatabase：单任务完整出图：CopyFrom 布局后配置设备/介质/窗口/比例并 RunPlot。 */
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
            ConfigurePlotScale(validator, plotSettings, plotWindow, job, settings.HideFrameBoundaryWhenPlotting);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media, job, plotWindow, deviceName));
            if (settings.HideFrameBoundaryWhenPlotting)
            {
                TryApplyHiddenFrameWindow(validator, plotSettings, plotWindow, job);
            }

            plotSettings.PlotTransparency = settings.PlotTransparency;

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
            ValidatePlotOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
        }
    }

    /** FindLayoutForJob：在事务中定位任务对应 Layout。 */
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

    /** RunPlot：PlotEngine 输出到文件并等待空闲。 */
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

    /** PreviewDatabase：组装预览用 PlotSettings 后 RunPreview。 */
    private static void PreviewDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document plotDocument)
    {
        WaitForPlotIdle();

        var oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var singleSettings = AppSettingsStore.Load();
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
            ConfigurePlotScale(validator, plotSettings, plotWindow, job, singleSettings.HideFrameBoundaryWhenPlotting);
            validator.SetPlotCentered(plotSettings, true);
            validator.SetPlotRotation(plotSettings, DetectRotation(media, job, plotWindow, deviceName));
            if (singleSettings.HideFrameBoundaryWhenPlotting)
            {
                TryApplyHiddenFrameWindow(validator, plotSettings, plotWindow, job);
            }

            plotSettings.PlotTransparency = singleSettings.PlotTransparency;

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

    /** RunPreview：创建预览引擎显示打印预览。 */
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
}
