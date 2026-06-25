using System;
using System.Linq;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands
{
    /// <summary>
    /// 将 PlotJob 的矩形窗口四个角点按矩阵变换到目标坐标系，重新取包围盒。
    /// 同时标记 IsDcsWindow=true，下游 GetPlotWindow 和 PrepareEditorViewForPlot 识别此标记。
    /// </summary>
    internal static void TransformPlotWindow(PlotJob job, Matrix3d toTarget)
    {
        var corners = new[]
        {
            new Point3d(job.MinX, job.MinY, 0).TransformBy(toTarget),
            new Point3d(job.MaxX, job.MinY, 0).TransformBy(toTarget),
            new Point3d(job.MinX, job.MaxY, 0).TransformBy(toTarget),
            new Point3d(job.MaxX, job.MaxY, 0).TransformBy(toTarget)
        };
        job.MinX = corners.Min(p => p.X);
        job.MinY = corners.Min(p => p.Y);
        job.MaxX = corners.Max(p => p.X);
        job.MaxY = corners.Max(p => p.Y);
        job.IsDcsWindow = true;
    }

    /// <summary>
    /// 构建 WCS → DCS 变换矩阵，等价于 ObjectARX 的 acedTrans(point, 0, 2)。
    /// 模型空间根据当前视图的 ViewDirection/Target/ViewTwist 构造；
    /// 图纸空间没有视图变换，返回单位矩阵。
    /// </summary>
    internal static Matrix3d BuildWcsToDcsMatrix(Editor editor)
    {
        // 图纸空间：DCS = WCS，无视图旋转
        if (!editor.Document.Database.TileMode)
        {
            return Matrix3d.Identity;
        }

        var view = editor.GetCurrentView();

        // 构造 DCS→WCS（显示→世界）：PlaneToWorld × Displacement × Rotation
        var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
        wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
        wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;

        // 取逆 → WCS→DCS
        return wcsToDcs.Inverse();
    }

    /// <summary>
    /// 构建 UCS → DCS 变换矩阵，等价于 ObjectARX 的 acedTrans(point, 1, 2)。
    /// = UCS→WCS × WCS→DCS
    /// </summary>
    private static Matrix3d BuildUcsToDcsMatrix(Editor editor)
    {
        // UCS → WCS：单位 UCS 时为单位矩阵，TransformBy 不起作用
        var ucsToWcs = editor.CurrentUserCoordinateSystem;

        try
        {
            var wcsToDcs = BuildWcsToDcsMatrix(editor);

            // PreMultiplyBy(A) = A × this，最终 = wcsToDcs × ucsToWcs
            return ucsToWcs.PreMultiplyBy(wcsToDcs);
        }
        catch (System.Exception ex)
        {
            // 取视图矩阵失败时退回 UCS→WCS
            editor.WriteMessage($"\nUCS→DCS 变换失败，退回 UCS→WCS：{ex.Message}");
            return ucsToWcs;
        }
    }
}
