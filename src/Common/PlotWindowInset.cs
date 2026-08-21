using System;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 将已确定的 DCS 打印窗口按纸面毫米四边内退。
/// 调用方必须先按原窗口完成选纸、旋转、留白和比例，再决定是否内退；
/// 内退不得回头参与比例或留白计算。
/// </summary>
internal static class PlotWindowInset
{
    /// <summary>不打印外边框时，打印内容四边各裁掉的纸面毫米。</summary>
    internal const double PaperInsetMm = 1.0;

    /// <summary>内退后纸面短边不足该值则放弃内退，避免窗口退化。</summary>
    internal const double MinimumRemainingShortSideMm = 2.0;

    /// <summary>
    /// 按当前打印策略计算原窗口对应的 mm/图面单位。
    /// 必须与 <c>ConfigurePlotScale</c> 使用同一套窗口和纸张，不能传入内退后的窗口。
    /// </summary>
    /// <param name="window">原 DCS 打印窗口。</param>
    /// <param name="paperWidthMm">当前介质宽度（毫米）。</param>
    /// <param name="paperHeightMm">当前介质高度（毫米）。</param>
    /// <param name="job">打印任务，用于读取留白与原图幅。</param>
    /// <returns>毫米/图面单位；无法计算时为 0。</returns>
    internal static double ResolveMillimetersPerDrawingUnit(
        Extents2d window,
        double paperWidthMm,
        double paperHeightMm,
        PlotJob job)
    {
        return ResolveMillimetersPerDrawingUnit(
            Math.Abs(window.MaxPoint.X - window.MinPoint.X),
            Math.Abs(window.MaxPoint.Y - window.MinPoint.Y),
            paperWidthMm,
            paperHeightMm,
            job);
    }

    /// <summary>
    /// 按当前打印策略计算原窗口对应的 mm/图面单位。
    /// </summary>
    /// <param name="windowWidth">原窗口宽度（图面单位）。</param>
    /// <param name="windowHeight">原窗口高度（图面单位）。</param>
    /// <param name="paperWidthMm">当前介质宽度（毫米）。</param>
    /// <param name="paperHeightMm">当前介质高度（毫米）。</param>
    /// <param name="job">打印任务，用于读取留白与原图幅。</param>
    /// <returns>毫米/图面单位；无法计算时为 0。</returns>
    internal static double ResolveMillimetersPerDrawingUnit(
        double windowWidth,
        double windowHeight,
        double paperWidthMm,
        double paperHeightMm,
        PlotJob job)
    {
        if (windowWidth <= 1e-12 || windowHeight <= 1e-12)
        {
            return 0;
        }

        if (!job.LeavePaperMargin)
        {
            var paperLong = Math.Max(paperWidthMm, paperHeightMm);
            var paperShort = Math.Min(paperWidthMm, paperHeightMm);
            var windowLong = Math.Max(windowWidth, windowHeight);
            var windowShort = Math.Min(windowWidth, windowHeight);
            if (paperShort <= 1e-12 || windowShort <= 1e-12)
            {
                return 0;
            }

            return Math.Min(paperLong / windowLong, paperShort / windowShort);
        }

        if (job.PaperMarginMm > 0d)
        {
            var originalShortMm = Math.Min(job.PaperWidthMm, job.PaperHeightMm);
            var windowShortSide = Math.Min(windowWidth, windowHeight);
            if (originalShortMm <= 1e-12 || windowShortSide <= 1e-12)
            {
                return 0;
            }

            return originalShortMm / windowShortSide;
        }

        var marginMm = Math.Abs(job.PaperMarginMm) > 0d ? Math.Abs(job.PaperMarginMm) : 1d;
        var paperShortSide = Math.Min(paperWidthMm, paperHeightMm);
        var usableShortSide = paperShortSide - marginMm * 2d;
        var windowShortForMargin = Math.Min(windowWidth, windowHeight);
        if (usableShortSide <= 1e-12 || windowShortForMargin <= 1e-12)
        {
            return 0;
        }

        return usableShortSide / windowShortForMargin;
    }

    /// <summary>
    /// 将 DCS 窗口四边各内退指定纸面毫米。窗口过小时返回 <see langword="false"/> 并保留原窗口。
    /// </summary>
    /// <param name="window">原 DCS 打印窗口。</param>
    /// <param name="millimetersPerDrawingUnit">已按原窗口算出的 mm/图面单位。</param>
    /// <param name="insetPaperMm">每边内退的纸面毫米。</param>
    /// <param name="insetWindow">内退后的窗口；失败时等于原窗口。</param>
    /// <returns>是否成功内退。</returns>
    internal static bool TryInsetByPaperMillimeters(
        Extents2d window,
        double millimetersPerDrawingUnit,
        double insetPaperMm,
        out Extents2d insetWindow)
    {
        insetWindow = window;
        if (millimetersPerDrawingUnit <= 1e-12 || insetPaperMm <= 0)
        {
            return false;
        }

        var drawingInset = insetPaperMm / millimetersPerDrawingUnit;
        var minX = Math.Min(window.MinPoint.X, window.MaxPoint.X);
        var minY = Math.Min(window.MinPoint.Y, window.MaxPoint.Y);
        var maxX = Math.Max(window.MinPoint.X, window.MaxPoint.X);
        var maxY = Math.Max(window.MinPoint.Y, window.MaxPoint.Y);
        var width = maxX - minX;
        var height = maxY - minY;
        if (width <= drawingInset * 2d || height <= drawingInset * 2d)
        {
            return false;
        }

        var remainingShortMm = Math.Min(width, height) * millimetersPerDrawingUnit - insetPaperMm * 2d;
        if (remainingShortMm < MinimumRemainingShortSideMm)
        {
            return false;
        }

        insetWindow = new Extents2d(
            minX + drawingInset,
            minY + drawingInset,
            maxX - drawingInset,
            maxY - drawingInset);
        return true;
    }
}
