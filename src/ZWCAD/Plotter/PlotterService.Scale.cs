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
 * @file PlotterService.Scale.cs（ZWCAD）
 * @description 打印比例与精确窗口比例。
 *
 * 主要功能：
 * - ConfigurePlotScale：布满图纸 / 自定义比例 / 精确窗口
 * - SetExactWindowScale：按窗口与可打印区域算 CustomScale
 * - TryApplyHiddenFrameWindow：隐藏外框时内退打印窗口
 *
 * 核心代码：
 * - LeavePaperMargin / UseExactWindowScale：对应对话框留白与精确比例选项
 * - ScaleToFit：布满图纸时由校验器计算比例
 *
 * 注意：只改比例与内退窗口；坐标系变换在 Window partial。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** ConfigurePlotScale：配置布满图纸、精确窗口或自定义比例。 */
    private static void ConfigurePlotScale(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        Extents2d window,
        PlotJob job,
        bool hideOuterFrame = false)
    {
        if (!job.LeavePaperMargin)
        {
            if (job.UseExactWindowScale || hideOuterFrame)
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

    /** SetExactWindowScale：按窗口与可打印区域写入 CustomScale。 */
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

    /**
     * TryApplyHiddenFrameWindow：
     * 在比例已按原窗口写入后，把打印窗口四边各内退 1mm 纸面。
     * 选纸、旋转和留白仍使用 originalWindow。
     */
    private static void TryApplyHiddenFrameWindow(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        Extents2d originalWindow,
        PlotJob job)
    {
        var paper = plotSettings.PlotPaperSize;
        var paperWidthMm = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var paperHeightMm = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        if (paperWidthMm <= 1e-9 || paperHeightMm <= 1e-9)
        {
            paperWidthMm = paper.X;
            paperHeightMm = paper.Y;
        }

        var millimetersPerDrawingUnit = PlotWindowInset.ResolveMillimetersPerDrawingUnit(
            originalWindow,
            paperWidthMm,
            paperHeightMm,
            job);
        if (!PlotWindowInset.TryInsetByPaperMillimeters(
                originalWindow,
                millimetersPerDrawingUnit,
                PlotWindowInset.PaperInsetMm,
                out var insetWindow))
        {
            return;
        }

        validator.SetPlotWindowArea(plotSettings, insetWindow);
    }
}
