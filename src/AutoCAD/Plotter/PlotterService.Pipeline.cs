using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

/**
 * @file PlotterService.Pipeline.cs（AutoCAD）
 * @description 出图主流程：组装 PlotSettings → 校验 → 执行 Plot/Preview。
 *
 * 主要功能：
 * - PlotDatabase / CreateValidatedPlot：单任务完整出图链路
 * - ConfigurePlotSettings：设备、样式、窗口、比例、旋转、着色
 * - RunPlot / PreviewDatabase / RunPreview：调用 PlotEngine
 *
 * 核心代码：
 * - CopyFrom(layout)：必须以布局为底，避免空 PlotSettings 导致栅格退回 Display
 * - ReassertRasterFitSettings：PNG/JPG 在 MatchEnabled 校验后重申 Window + ScaleToFit
 * - ShadePlot=Wireframe：栅格禁止「按显示」，否则轴测视图会打成倾斜小图
 *
 * 注意：PDF/PNG/JPG 共用本管道，仅换绘图仪；勿单独改栅格窗口逻辑。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** PlotDatabase：单任务完整出图：取布局与窗口 → 校验设置 → RunPlot → 校验输出文件。 */
    private static void PlotDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, AppSettings settings, Document? plotDocument)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            throw new InvalidOperationException("未找到可用的输出设备。");
        }

        WaitForPlotIdle();

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(
                layout,
                job,
                window,
                deviceName,
                styleSheet,
                settings.HideFrameBoundaryWhenPlotting,
                settings.PlotTransparency);

            PrepareOutputFile(job.OutputPath);
            RunPlot(plot.Info, documentName, job.OutputPath, job.DrawingNumber);
            tr.Commit();
            WaitForPlotIdle();
            ValidatePlotOutput(job.OutputPath);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    /** CreateValidatedPlot：创建已校验打印包；缓存介质失效时清目录并重试一次。 */
    private static ValidatedPlot CreateValidatedPlot(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet,
        bool hideOuterFrame,
        bool plotTransparency)
    {
        try
        {
            return CreateValidatedPlotCore(layout, job, window, deviceName, styleSheet, hideOuterFrame, plotTransparency);
        }
        catch (CachedMediaCatalogException)
        {
            // PC3/PMP 可能在 CAD 会话中被更新；仅当缓存目录失效时清缓存并完整读取一次。
            InvalidateMediaCatalog(deviceName);
            return CreateValidatedPlotCore(layout, job, window, deviceName, styleSheet, hideOuterFrame, plotTransparency);
        }
    }

    /** CreateValidatedPlotCore：核心：CopyFrom 布局后配置设备/介质/窗口/比例，校验并处理栅格二次申明。 */
    private static ValidatedPlot CreateValidatedPlotCore(
        Layout layout,
        PlotJob job,
        Extents2d window,
        string deviceName,
        string styleSheet,
        bool hideOuterFrame,
        bool plotTransparency)
    {
        var validator = PlotSettingsValidator.Current;
        var media = ChooseMedia(validator, layout, deviceName, job, window, out var usedCachedCatalog);
        media.FromCachedCatalog = usedCachedCatalog;
        var errors = new List<string>();

        var preferredRotation = ResolvePlotRotation(deviceName, media.PreferredRotation, job, window);
        foreach (var rotation in RotationOrder(preferredRotation))
        {
            var settings = new PlotSettings(layout.ModelType);
            try
            {
                settings.CopyFrom(layout);
                ConfigurePlotSettings(
                    validator,
                    settings,
                    deviceName,
                    styleSheet,
                    media,
                    rotation,
                    window,
                    job,
                    hideOuterFrame,
                    plotTransparency);

                var info = new PlotInfo
                {
                    Layout = layout.ObjectId,
                    OverrideSettings = settings
                };

                new PlotInfoValidator
                {
                    MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                }.Validate(info);

                if (IsRasterPlotDevice(deviceName))
                {
                    // 窗口与 PDF 相同；问题在校验阶段：MatchEnabled 常把 PNG/JPG 的「布满图纸」
                    // 改成错误自定义比例（对话框可见 1 pixel = 25.4），结果大白纸上内容极小。
                    // 这里在校验后强制恢复 Window + 比例，再校验一次写入 ValidatedSettings。
                    ReassertRasterFitSettings(
                        validator, settings, window, job, deviceName, hideOuterFrame);
                    new PlotInfoValidator
                    {
                        MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                    }.Validate(info);
                }
                else if (job.RequireExactPaperSize
                    && job.UseExactWindowScale
                    && settings.PlotPaperUnits == PlotPaperUnit.Inches)
                {
                    // AutoCAD 2024 会在 PlotInfo 校验阶段把部分毫米自定义介质重新匹配为英寸介质。
                    // 必须按最终单位重写比例并重新居中，否则毫米分子会被按英寸解释，内容放大 25.4 倍。
                    ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame);
                    ResetAndCenterPlot(validator, settings);
                    new PlotInfoValidator
                    {
                        MediaMatchingPolicy = MatchingPolicy.MatchEnabled
                    }.Validate(info);
                }

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

        var failure = new InvalidOperationException(
            "AutoCAD 不接受当前打印设置。"
            + $" 图纸={job.DrawingNumber}_{job.Title};"
            + $" 目标纸张={job.PaperWidthMm:0.##}x{job.PaperHeightMm:0.##}mm;"
            + $" 窗口=({window.MinPoint.X:0.###},{window.MinPoint.Y:0.###})-({window.MaxPoint.X:0.###},{window.MaxPoint.Y:0.###});"
            + " 尝试结果=" + string.Join(" | ", errors));
        if (media.FromCachedCatalog)
        {
            throw new CachedMediaCatalogException(failure);
        }

        throw failure;
    }

    /** ConfigurePlotSettings：写入设备、纸张单位、介质、Window、比例、旋转、样式与栅格线框着色。 */
    private static void ConfigurePlotSettings(
        PlotSettingsValidator validator,
        PlotSettings settings,
        string deviceName,
        string styleSheet,
        MediaChoice media,
        PlotRotation rotation,
        Extents2d window,
        PlotJob job,
        bool hideOuterFrame,
        bool plotTransparency)
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
        var paperUnit = IsRasterPlotDevice(deviceName)
            ? PlotPaperUnit.Pixels
            : PlotPaperUnit.Millimeters;
        // AutoCAD 的 PNG/JPG 栅格设备只接受 Pixels；强制设为 Millimeters 会直接抛出 eInvalidInput。
        validator.SetPlotPaperUnits(settings, paperUnit);
        if (media.UseClosestBySize)
        {
            validator.SetClosestMediaName(settings, media.WidthMm, media.HeightMm, PlotPaperUnit.Millimeters, false);
        }
        else
        {
            validator.SetCanonicalMediaName(settings, media.Name);
        }

        EnsureRequiredMediaSize(settings, media, deviceName);
        // AutoCAD 2024 的部分布局在尚无有效窗口时直接切换为 Window 会抛 eInvalidInput。
        // 必须先写入 DCS 窗口，再切换打印类型；柱状图 299x212mm 自定义纸已做宿主验证。
        validator.SetPlotWindowArea(settings, window);
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
        ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame);
        validator.SetPlotRotation(settings, rotation);
        // 初次校验前只按最终旋转居中。部分 90° 自定义介质此时不接受显式 SetPlotOrigin(0,0)。
        // 若校验后单位被改成 Inches，再在上面的二次校验分支统一清零原点并重新居中。
        validator.SetPlotCentered(settings, true);

        if (!string.IsNullOrWhiteSpace(styleSheet))
        {
            validator.SetCurrentStyleSheet(settings, styleSheet);
        }

        // 比例和留白已按原窗口写入；内退只替换打印窗口，避免 ScaleToFit 把裁切后的内容重新铺满纸面。
        if (hideOuterFrame)
        {
            TryApplyHiddenFrameWindow(validator, settings, window, job, deviceName);
        }

        settings.PlotTransparency = plotTransparency;
        if (IsRasterPlotDevice(deviceName))
        {
            // CopyFrom(layout) 会带入「按显示」着色；栅格驱动按显示投影时，轴测画面会打成倾斜小图。
            // PDF 矢量输出不受此影响。PNG/JPG 固定线框，与窗口正交打印一致。
            try
            {
                settings.ShadePlot = PlotSettingsShadePlotType.Wireframe;
            }
            catch
            {
            }
        }
    }

    /** ReassertRasterFitSettings：栅格设备在 PlotInfo 校验后重申 Window 与比例，避免 ValidatedSettings 丢掉「布满图纸」。 */
    private static void ReassertRasterFitSettings(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        PlotJob job,
        string deviceName,
        bool hideOuterFrame)
    {
        validator.SetPlotPaperUnits(settings, PlotPaperUnit.Pixels);
        validator.SetPlotWindowArea(settings, window);
        validator.SetPlotType(settings, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
        ConfigurePlotScale(validator, settings, window, job, deviceName, hideOuterFrame);
        if (hideOuterFrame)
        {
            TryApplyHiddenFrameWindow(validator, settings, window, job, deviceName);
        }

        ResetAndCenterPlot(validator, settings);
        try
        {
            settings.ShadePlot = PlotSettingsShadePlotType.Wireframe;
        }
        catch
        {
        }
    }

    /** RunPlot：调用 PlotEngine 把 PlotInfo 输出到文件，并等待引擎空闲。 */
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

    /** PreviewDatabase：预览路径：组装与正式出图相同的设置后调用 RunPreview。 */
    private static void PreviewDatabase(Database db, string documentName, PlotJob job, string deviceName, string styleSheet, Document plotDocument)
    {
        WaitForPlotIdle();

        var oldDatabase = HostApplicationServices.WorkingDatabase;
        HostApplicationServices.WorkingDatabase = db;
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var settings = AppSettingsStore.Load();
            var layout = FindLayoutForJob(tr, db, job);
            var window = GetPlotWindow(job, plotDocument);
            using var plot = CreateValidatedPlot(
                layout,
                job,
                window,
                deviceName,
                styleSheet,
                settings.HideFrameBoundaryWhenPlotting,
                settings.PlotTransparency);
            RunPreview(plot.Info, documentName);
            tr.Commit();
            WaitForPlotIdle();
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = oldDatabase;
        }
    }

    /** RunPreview：创建预览引擎显示打印预览对话框。 */
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
