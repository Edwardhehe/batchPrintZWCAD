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
 * @file PlotterService.Scale.cs（AutoCAD）
 * @description 打印比例与「布满图纸 / 精确窗口」配置。
 *
 * 主要功能：
 * - ConfigurePlotScale：按任务选项选择布满、自定义比例或精确窗口
 * - SetExactWindowScale / TryApplyHiddenFrameWindow：精确比例与隐藏外框内退窗口
 * - ToPlotPaperUnitScale：把图面比例换算成 PlotPaperUnits 下的数值
 *
 * 核心代码：
 * - ToPlotPaperUnitScale：Pixels 单位必须按 DPI 换算，不能把毫米当像素
 * - ScaleToFit / CustomScale：与对话框「布满图纸」选项对应
 *
 * 注意：改比例时勿动窗口坐标系；窗口来自 Window partial。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** ConfigurePlotScale：按任务配置布满图纸、精确窗口比例或自定义比例。 */
    private static void ConfigurePlotScale(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        PlotJob job,
        string deviceName,
        bool hideOuterFrame = false)
    {
        if (!job.LeavePaperMargin)
        {
            if (job.UseExactWindowScale || hideOuterFrame)
            {
                SetExactWindowScale(validator, settings, window, deviceName);
                return;
            }

            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
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
            validator.SetUseStandardScale(settings, false);
            validator.SetCustomPrintScale(
                settings, new CustomScale(ToPlotPaperUnitScale(settings, scale, deviceName), 1d));
            return;
        }

        // ── 缩比例模式（PaperMarginMm < 0 时 abs 取值） ──
        var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
        var paperSize = GetPlotPaperSizeMm(settings, deviceName);
        var paperShortSide = Math.Min(paperSize.X, paperSize.Y);
        var windowShort = Math.Min(windowWidth, windowHeight);
        var usableShortSide = paperShortSide - marginMm * 2d;

        if (usableShortSide <= 0d)
            throw new InvalidOperationException("留白值过大导致可用纸张面积为零，请减小留白距离。");

        // 保持原图框窗口不变，只缩小打印比例。扩大窗口会把图框外的相邻对象带入 PDF。
        var scaleReduced = usableShortSide / windowShort;
        validator.SetUseStandardScale(settings, false);
        validator.SetCustomPrintScale(
            settings, new CustomScale(ToPlotPaperUnitScale(settings, scaleReduced, deviceName), 1d));
    }

    /** SetExactWindowScale：按打印窗口与可打印区域计算 CustomScale（精确窗口）。 */
    private static void SetExactWindowScale(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d window,
        string deviceName)
    {
        var paper = GetPlotPaperSizeMm(settings, deviceName);
        var windowWidth = Math.Abs(window.MaxPoint.X - window.MinPoint.X);
        var windowHeight = Math.Abs(window.MaxPoint.Y - window.MinPoint.Y);
        var paperLong = Math.Max(paper.X, paper.Y);
        var paperShort = Math.Min(paper.X, paper.Y);
        var windowLong = Math.Max(windowWidth, windowHeight);
        var windowShort = Math.Min(windowWidth, windowHeight);
        if (paperShort <= 0d || windowShort <= 0d)
            throw new InvalidOperationException("任意纸张或打印窗口尺寸无效，无法计算精确打印比例。");

        var scale = Math.Min(paperLong / windowLong, paperShort / windowShort);
        validator.SetUseStandardScale(settings, false);
        validator.SetCustomPrintScale(
            settings, new CustomScale(ToPlotPaperUnitScale(settings, scale, deviceName), 1d));
    }

    /**
     * TryApplyHiddenFrameWindow：
     * 在比例已按原窗口写入后，把打印窗口四边各内退 1mm 纸面。
     * 选纸、旋转和留白仍使用 originalWindow。
     */
    private static void TryApplyHiddenFrameWindow(
        PlotSettingsValidator validator,
        PlotSettings settings,
        Extents2d originalWindow,
        PlotJob job,
        string deviceName)
    {
        var paper = GetPlotPaperSizeMm(settings, deviceName);
        var millimetersPerDrawingUnit = PlotWindowInset.ResolveMillimetersPerDrawingUnit(
            originalWindow,
            paper.X,
            paper.Y,
            job);
        if (!PlotWindowInset.TryInsetByPaperMillimeters(
                originalWindow,
                millimetersPerDrawingUnit,
                PlotWindowInset.PaperInsetMm,
                out var insetWindow))
        {
            return;
        }

        validator.SetPlotWindowArea(settings, insetWindow);
    }

    /**
     * ToPlotPaperUnitScale：
     * 把「每图面单位对应的毫米」换成当前 PlotPaperUnits 下 CustomScale 的分子。
     * Pixels 必须按 DPI 换成 px/图面单位，不能把毫米数直接当像素用。
     */
    private static double ToPlotPaperUnitScale(
        PlotSettings settings,
        double millimetersPerDrawingUnit,
        string deviceName)
    {
        if (settings.PlotPaperUnits == PlotPaperUnit.Inches)
        {
            return millimetersPerDrawingUnit / 25.4d;
        }

        if (settings.PlotPaperUnits == PlotPaperUnit.Pixels)
        {
            var dpi = AcadPlotterInstaller.GetRasterDpi(deviceName);
            var dpiValue = Math.Max(dpi.X, dpi.Y);
            if (dpiValue <= 0d)
            {
                dpiValue = 100d;
            }

            return millimetersPerDrawingUnit * dpiValue / 25.4d;
        }

        return millimetersPerDrawingUnit;
    }

    /** ResetAndCenterPlot：清零原点并重新居中，用于校验后单位被改成英寸时的修正。 */
    private static void ResetAndCenterPlot(PlotSettingsValidator validator, PlotSettings settings)
    {
        // CopyFrom(layout) 可能带入旧偏移；旋转、比例和窗口全部确定后再清零并居中。
        validator.SetPlotCentered(settings, false);
        validator.SetPlotOrigin(settings, Point2d.Origin);
        validator.SetPlotCentered(settings, true);
    }
}
