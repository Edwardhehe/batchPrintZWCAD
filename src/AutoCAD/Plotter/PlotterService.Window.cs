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
 * @file PlotterService.Window.cs（AutoCAD）
 * @description 打印窗口、视图准备与纸张旋转判定。
 *
 * 主要功能：
 * - GetPlotWindow：任务窗口 → 打印用 Extents2d（含 UCS/DCS）
 * - PrepareEditorViewForPlot：平面图固定俯视，扭转只用 ViewTwist
 * - ResolvePlotRotation / ResolveWindowRotation：纸向与窗口横竖对齐
 *
 * 核心代码：
 * - GetWorldToDisplayMatrix：WCS→显示坐标；旋转 UCS 须用真实四角而非包围盒二次放大
 * - PrepareEditorViewForPlot：ViewDirection 固定俯视，避免三维/轴测误打
 *
 * 注意：PDF 与 PNG/JPG 共用窗口逻辑；修栅格问题优先查 Pipeline/Scale，勿改这里。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** ResolvePlotRotation：解析最终打印旋转；PNG/JPG 与 PDF 共用窗口横竖兜底。 */
    private static PlotRotation ResolvePlotRotation(
        string deviceName,
        PlotRotation paperRotation,
        PlotJob job,
        Extents2d window)
    {
        // PNG/JPG 与 PDF 同一套旋转判断：介质 PreferredRotation + 窗口横竖兜底。
        _ = deviceName;
        return ResolveWindowRotation(paperRotation, job, window);
    }

    /** ResolveWindowRotation：纸向与窗口横竖不一致时切换 90°。 */
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

        return ToggleQuarterTurn(paperRotation);
    }

    /** ToggleQuarterTurn：在 0↔90、180↔270 间切换四分之一转。 */
    private static PlotRotation ToggleQuarterTurn(PlotRotation paperRotation)
    {
        return paperRotation switch
        {
            PlotRotation.Degrees000 => PlotRotation.Degrees090,
            PlotRotation.Degrees090 => PlotRotation.Degrees000,
            PlotRotation.Degrees180 => PlotRotation.Degrees270,
            PlotRotation.Degrees270 => PlotRotation.Degrees180,
            _ => paperRotation
        };
    }

    /** PrepareEditorViewForPlot：出图前校正视图：平面图俯视，扭转只用 ViewTwist。 */
    private static void PrepareEditorViewForPlot(Document doc, PlotJob job)
    {
        // 与 PDF 相同：图纸空间跳过；前端已生成 DCS 窗口时保留当前视图，不再二次改视线。
        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

        try
        {
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
            // 平面图始终俯视；UCS 只反映为 ViewTwist，不把视线拧成轴测。
            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = job.UsesUserCoordinateSystem
                ? -Math.Atan2(job.UcsXAxisY, job.UcsXAxisX)
                : 0;
            view.Target = center;
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

    /** GetPlotWindow：任务窗口转为打印 Extents2d；旋转 UCS 用真实四角变换。 */
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
                // 使用真实四角；旋转 UCS 下的 WCS 包围盒四角会把窗口再次放大。
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
            }
        }

        return new Extents2d(
            Math.Min(job.MinX, job.MaxX),
            Math.Min(job.MinY, job.MaxY),
            Math.Max(job.MinX, job.MaxX),
            Math.Max(job.MinY, job.MaxY));
    }

    /** GetWorldToDisplayMatrix：由当前视图构造 WCS→显示坐标变换矩阵。 */
    private static Matrix3d GetWorldToDisplayMatrix(ViewTableRecord view)
    {
        var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
        matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
        matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
        return matrix.Inverse();
    }
}
