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
 * @file PlotterService.Window.cs（ZWCAD）
 * @description 打印窗口变换、编辑器视图准备与旋转检测。
 *
 * 主要功能：
 * - GetPlotWindow：WCS/DCS 窗口到 Extents2d
 * - PrepareEditorViewForPlot：出图前校正视图方向与扭转
 * - DetectRotation / ToggleQuarterTurn：纸张与窗口朝向匹配
 *
 * 核心代码：
 * - IsDcsWindow：单张打印已完成 UCS 到 DCS 时跳过二次变换
 * - GetWorldToDisplayMatrix：批量路径的 WCS 到显示坐标
 *
 * 注意：单张与批量窗口来源不同；改变换前先确认 job.IsDcsWindow。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** GetPlotWindow：任务窗口→Extents2d；IsDcsWindow 时跳过二次 WCS→DCS。 */
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
                // UCS 任务必须从保存的 UCS 矩形重建真实 WCS 四角，不能使用 WCS 包围盒四角。
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

    /** GetWorldToDisplayMatrix：当前视图的 WCS→显示坐标矩阵。 */
    private static Matrix3d GetWorldToDisplayMatrix(ViewTableRecord view)
    {
        var matrix = Matrix3d.PlaneToWorld(view.ViewDirection);
        matrix = Matrix3d.Displacement(view.Target - Point3d.Origin) * matrix;
        matrix = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * matrix;
        return matrix.Inverse();
    }

    /** PrepareEditorViewForPlot：出图前校正视图方向与扭转。 */
    private static void PrepareEditorViewForPlot(Document doc, PlotJob job)
    {
        // 图纸空间无视图概念，跳过。
        // 已生成 DCS 的任务必须保留生成时的视图；UCS 任务没有提前生成 DCS，必须在这里恢复扫描时的 UCS 视图。
        if (job.IsPaperSpace || job.IsDcsWindow)
        {
            return;
        }

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
        if (job.UsesUserCoordinateSystem)
        {
            var xAxis = new Vector3d(job.UcsXAxisX, job.UcsXAxisY, job.UcsXAxisZ).GetNormal();
            var yAxis = new Vector3d(job.UcsYAxisX, job.UcsYAxisY, job.UcsYAxisZ).GetNormal();
            view.ViewDirection = xAxis.CrossProduct(yAxis).GetNormal();
            view.ViewTwist = -Math.Atan2(xAxis.Y, xAxis.X);
        }
        else
        {
            view.ViewDirection = Vector3d.ZAxis;
            view.ViewTwist = 0;
        }

        view.Target = center;
        view.CenterPoint = Point2d.Origin;
        view.Width = width * 1.05;
        view.Height = height * 1.05;
        doc.Editor.SetCurrentView(view);
    }

    /** DetectRotation：按纸向与窗口横竖检测是否需旋转。 */
    private static PlotRotation DetectRotation(
        MediaSelection? media,
        PlotJob job,
        Extents2d window,
        string deviceName)
    {
        var paperRotation = media?.NeedsRotation == true
            ? PlotRotation.Degrees090
            : PlotRotation.Degrees000;
        if (IsRasterPlotDevice(deviceName))
        {
            // 栅格目标方向来自 PlotWindowArea 使用的同一 DCS 窗口，禁止再用 WCS 或默认纸张方向翻转。
            return paperRotation;
        }

        // 扩大纸张模式下以有效尺寸（含留白）判断横竖方向，保证与实际纸张方向一致。
        var paperWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var paperHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
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

    /** ToggleQuarterTurn：0↔90、180↔270 切换。 */
    private static PlotRotation ToggleQuarterTurn(PlotRotation paperRotation)
    {
        return paperRotation == PlotRotation.Degrees090
            ? PlotRotation.Degrees000
            : PlotRotation.Degrees090;
    }
}
