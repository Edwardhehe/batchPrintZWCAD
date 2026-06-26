using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class BatchPlotCommands : IExtensionApplication
{
    private static BatchPlotForm? _batchPlotForm;
    private static RectangleBatchPlotForm? _rectangleBatchPlotForm;

    // ---- 插件生命周期 ----

    public void Initialize()
    {
        if (IsCoreConsole())
        {
            return;
        }

        AcadPlotterInstaller.InstallBundledPlotter();
        CadMenuInstaller.Install(force: true);
    }

    public void Terminate()
    {
    }

    private static bool IsCoreConsole()
    {
        try
        {
            var processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            return string.Equals(processName, "accoreconsole", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ---- 命令入口 ----

    [CommandMethod("ZBP_ADD_TITLE_BLOCK")]
    public void AddTitleBlock() => AddTitleBlockCore();

    [CommandMethod("_ZBP_INTERNAL_ADD_TITLE_BLOCK")]
    public void AddTitleBlockLegacy() => AddTitleBlockCore();

    [CommandMethod("ZBP_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindow() => ShowBatchPlotWindowCore();

    [CommandMethod("_ZBP_INTERNAL_SHOW_PANEL", CommandFlags.Session)]
    public void ShowBatchPlotWindowLegacy() => ShowBatchPlotWindowCore();

    [CommandMethod("ZBP_SINGLE_PLOT", CommandFlags.Session)]
    public void SinglePlot() => SinglePlotCore();

    [CommandMethod("_ZBP_INTERNAL_SINGLE_PLOT", CommandFlags.Session)]
    public void SinglePlotLegacy() => SinglePlotCore();

    [CommandMethod("ZBP_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void RectangleBatchPlot() => ShowRectangleBatchPlotCore();

    [CommandMethod("_ZBP_INTERNAL_RECTANGLE_BATCH_PLOT", CommandFlags.Session)]
    public void RectangleBatchPlotLegacy() => ShowRectangleBatchPlotCore();

    [CommandMethod("ZBP_OPEN_CONFIG")]
    public void OpenConfigDirectory()
    {
        Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
        System.Diagnostics.Process.Start(TitleBlockLibraryStore.DefaultDirectory);
    }

    [CommandMethod("_ZBP_INTERNAL_OPEN_CONFIG")]
    public void OpenConfigDirectoryLegacy() => OpenConfigDirectory();

    [CommandMethod("ZBP_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibrary()
    {
        using var form = new TitleBlockLibraryManagerForm();
        ShowModalDialog(form);
    }

    [CommandMethod("_ZBP_INTERNAL_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibraryLegacy() => ManageLibrary();

    [CommandMethod("ZBP_SETTINGS", CommandFlags.Session)]
    public void ShowSettings()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        using var form = new SettingsForm(doc);
        if (ShowModalDialog(form) == DialogResult.OK && form.RequestPickDirectoryCellSizes && doc != null)
        {
            var settings = AppSettingsStore.Load();
            var ok = DirectoryTableGenerator.PromptCellSizes(doc, settings, out _, out var message);
            MessageBox.Show(message, "批量打印设置", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_SETTINGS", CommandFlags.Session)]
    public void ShowSettingsLegacy() => ShowSettings();

    [CommandMethod("ZBP_RELOAD_MENU")]
    public void ReloadMenu() => CadMenuInstaller.Install(force: true);

    [CommandMethod("_ZBP_INTERNAL_RELOAD_MENU")]
    public void ReloadMenuLegacy() => ReloadMenu();

    [CommandMethod("ZBP_INSTALL_AUTOLOAD")]
    public void InstallAutoload()
    {
        try
        {
            var roots = AutoloadManager.Install();
            MessageBox.Show(
                "已安装自动加载。\n\n下次启动AutoCAD会自动加载批量打印插件。\n\n写入位置:\n" + string.Join("\n", roots),
                "批量打印",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("安装自动加载失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_INSTALL_AUTOLOAD")]
    public void InstallAutoloadLegacy() => InstallAutoload();

    [CommandMethod("ZBP_UNINSTALL_AUTOLOAD")]
    public void UninstallAutoload()
    {
        try
        {
            var removed = AutoloadManager.Uninstall();
            MessageBox.Show(
                removed > 0
                    ? "已卸载自动加载。\n\n当前已加载的插件会在本次CAD会话继续可用，关闭CAD后不会再自动加载。"
                    : "没有找到已安装的自动加载项。",
                "批量打印",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("卸载自动加载失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [CommandMethod("_ZBP_INTERNAL_UNINSTALL_AUTOLOAD")]
    public void UninstallAutoloadLegacy() => UninstallAutoload();

    // 以下核心方法已拆分到 partial class 文件：
    //   AddTitleBlockCore     → AddTitleBlockCommands.cs
    //   SinglePlotCore        → SinglePlotCommands.cs
    //   TransformPlotWindow   → CoordinateUtils.cs
    //   BuildWcsToDcsMatrix   → CoordinateUtils.cs
    //   BuildUcsToDcsMatrix   → CoordinateUtils.cs

    // ---- 批量打印面板（图框库匹配） ----

    private static void ShowBatchPlotWindowCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_batchPlotForm is { IsDisposed: false })
        {
            _batchPlotForm.Activate();
            return;
        }

        var form = new BatchPlotForm(doc);
        _batchPlotForm = form;
        form.FormClosed += (_, _) =>
        {
            try
            {
                if (form.HasPendingPrint)
                {
                    form.ExecutePendingPrint();
                }
            }
            finally
            {
                if (ReferenceEquals(_batchPlotForm, form))
                {
                    _batchPlotForm = null;
                }

                form.Dispose();
            }
        };

        ShowModelessDialog(form);
    }

    // ---- 矩形框批量打印 ----

    private static void ShowRectangleBatchPlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_rectangleBatchPlotForm is { IsDisposed: false })
        {
            _rectangleBatchPlotForm.Activate();
            return;
        }

        var form = new RectangleBatchPlotForm(doc);
        _rectangleBatchPlotForm = form;
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_rectangleBatchPlotForm, form))
            {
                _rectangleBatchPlotForm = null;
            }

            form.Dispose();
        };
        ShowModelessDialog(form);
    }

    // ---- 通用工具方法 ----

    private static void RevealFileInExplorer(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{Path.GetFullPath(filePath)}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // PDF 已成功生成；资源管理器打开失败不应把打印标记为失败
        }
    }

    private static DialogResult ShowModalDialog(Form form)
    {
#if ACAD_CORE
        return form.ShowDialog();
#else
        return CadApp.ShowModalDialog(form);
#endif
    }

    private static void ShowModelessDialog(Form form)
    {
#if ACAD_CORE
        form.Show();
#else
        CadApp.ShowModelessDialog(form);
#endif
    }

    private static bool TryGetRegion(Editor editor, string firstPrompt, string secondPrompt, Matrix3d inverseBlockTransform, out LocalRectangle region)
    {
        region = new LocalRectangle();
        var first = editor.GetPoint(new PromptPointOptions(firstPrompt));
        if (first.Status != PromptStatus.OK)
        {
            return false;
        }

        var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
        var second = editor.GetCorner(cornerOptions);
        if (second.Status != PromptStatus.OK)
        {
            return false;
        }

        var p1 = first.Value.TransformBy(inverseBlockTransform);
        var p2 = second.Value.TransformBy(inverseBlockTransform);
        region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        return true;
    }

    private static OptionalRegionStatus TryGetOptionalRegion(
        Editor editor,
        string firstPrompt,
        string secondPrompt,
        Matrix3d inverseBlockTransform,
        out LocalRectangle region)
    {
        region = new LocalRectangle();
        var firstOptions = new PromptPointOptions(firstPrompt)
        {
            AllowNone = true
        };
        var first = editor.GetPoint(firstOptions);
        if (first.Status == PromptStatus.None)
        {
            return OptionalRegionStatus.None;
        }

        if (first.Status != PromptStatus.OK)
        {
            return OptionalRegionStatus.Cancel;
        }

        var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
        var second = editor.GetCorner(cornerOptions);
        if (second.Status != PromptStatus.OK)
        {
            return OptionalRegionStatus.Cancel;
        }

        var p1 = first.Value.TransformBy(inverseBlockTransform);
        var p2 = second.Value.TransformBy(inverseBlockTransform);
        region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);
        return OptionalRegionStatus.Selected;
    }

    private static bool TryGetBlockExtents(Database database, ObjectId blockReferenceId, out Extents3d extents)
    {
        extents = default;
        using var tr = database.TransactionManager.StartTransaction();
        var blockRef = (BlockReference)tr.GetObject(blockReferenceId, OpenMode.ForRead);

        try
        {
            var direct = blockRef.GeometricExtents;
            if (HasValidExtents(direct))
            {
                extents = direct;
                return true;
            }
        }
        catch
        {
        }

        var hasExtents = false;
        var combined = default(Extents3d);
        var definition = (BlockTableRecord)tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead);
        foreach (ObjectId entityId in definition)
        {
            try
            {
                var entity = tr.GetObject(entityId, OpenMode.ForRead) as Entity;
                if (entity == null)
                {
                    continue;
                }

                var transformed = TransformWorldExtents(entity.GeometricExtents, blockRef.BlockTransform);
                if (!HasValidExtents(transformed))
                {
                    continue;
                }

                if (!hasExtents)
                {
                    combined = transformed;
                    hasExtents = true;
                }
                else
                {
                    combined.AddExtents(transformed);
                }
            }
            catch
            {
            }
        }

        if (!hasExtents || !HasValidExtents(combined))
        {
            return false;
        }

        extents = combined;
        return true;
    }

    private static bool HasValidExtents(Extents3d extents)
    {
        var values = new[]
        {
            extents.MinPoint.X, extents.MinPoint.Y,
            extents.MaxPoint.X, extents.MaxPoint.Y
        };
        return values.All(value => !double.IsNaN(value) && !double.IsInfinity(value))
            && extents.MaxPoint.X - extents.MinPoint.X > 1e-6
            && extents.MaxPoint.Y - extents.MinPoint.Y > 1e-6;
    }

    private static Extents3d TransformWorldExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, extents.MinPoint.Z).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, extents.MaxPoint.Z).TransformBy(transform)
        };

        return new Extents3d(
            new Point3d(points.Min(p => p.X), points.Min(p => p.Y), points.Min(p => p.Z)),
            new Point3d(points.Max(p => p.X), points.Max(p => p.Y), points.Max(p => p.Z)));
    }

    private static Extents3d TransformRegion(LocalRectangle region, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(region.MinX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MinX, region.MaxY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MinY, 0).TransformBy(transform),
            new Point3d(region.MaxX, region.MaxY, 0).TransformBy(transform)
        };

        var minX = Math.Min(Math.Min(points[0].X, points[1].X), Math.Min(points[2].X, points[3].X));
        var minY = Math.Min(Math.Min(points[0].Y, points[1].Y), Math.Min(points[2].Y, points[3].Y));
        var maxX = Math.Max(Math.Max(points[0].X, points[1].X), Math.Max(points[2].X, points[3].X));
        var maxY = Math.Max(Math.Max(points[0].Y, points[1].Y), Math.Max(points[2].Y, points[3].Y));
        return new Extents3d(new Point3d(minX, minY, 0), new Point3d(maxX, maxY, 0));
    }

    private static LocalRectangle TransformExtents(Extents3d extents, Matrix3d transform)
    {
        var points = new[]
        {
            new Point3d(extents.MinPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MinPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MinPoint.Y, 0).TransformBy(transform),
            new Point3d(extents.MaxPoint.X, extents.MaxPoint.Y, 0).TransformBy(transform)
        };

        return LocalRectangle.FromPoints(
            points.Min(p => p.X),
            points.Min(p => p.Y),
            points.Max(p => p.X),
            points.Max(p => p.Y));
    }

    private static LocalRectangle ToFrameRelative(LocalRectangle region, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            region.MinX - referenceFrame.MinX,
            region.MinY - referenceFrame.MinY,
            region.MaxX - referenceFrame.MinX,
            region.MaxY - referenceFrame.MinY);
    }

    private static void AddBlockLog(string message)
    {
        try
        {
            var logDirectory = Path.Combine(TitleBlockLibraryStore.DefaultDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "AddTitleBlock_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine);
        }
        catch
        {
        }
    }

    private enum OptionalRegionStatus
    {
        Cancel,
        None,
        Selected
    }
}
