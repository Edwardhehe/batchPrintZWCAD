using System;
using System.Drawing;
using System.Collections.Generic;
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
        // 新用户首次加载时生成软件自有的栅格绘图器，并立即刷新当前 CAD 会话的设备缓存。
        AcadPlotterInstaller.InstallPngPlotter();
        AcadPlotterInstaller.InstallJpgPlotter();
        AcadPlotterInstaller.RefreshPlotterDevices();
        CadMenuInstaller.Install();
        // 每次加载时恢复用户设置的简化命令到 PGP 文件（Power 等重置 PGP 后自动修复）。
        CommandAliasManager.Apply(AppSettingsStore.Load().CommandAliases, out _);
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
        while (true)
        {
            using var form = new SettingsForm();
            if (ShowModalDialog(form) != DialogResult.OK)
            {
                return;
            }

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && !form.RequestPickScaleFromCad
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                return;
            }

            // 设置窗关闭后用户可能已切换文档；点选前再次刷新，且不复用设置窗构造时的文档引用。
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show(
                    "当前没有可用的 CAD 图纸，请先打开图纸后重试。",
                    "批量打印设置",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                continue;
            }

            var settings = AppSettingsStore.Load();
            bool ok;
            string message;
            if (form.RequestPickScaleFromCad)
            {
                ok = ScaleSettingsPicker.PromptScaleFromFrame(doc, settings, out settings, out message);
            }
            else if (form.RequestPickDirectoryTextAppearance)
            {
                ok = DirectoryTableGenerator.PromptTextAppearance(doc, settings, out _, out message);
            }
            else if (form.RequestPickDirectoryRowHeight)
            {
                ok = DirectoryTableGenerator.PromptRowHeight(doc, settings, out _, out message);
            }
            else
            {
                ok = DirectoryTableGenerator.PromptColumnSize(
                    doc,
                    settings,
                    form.RequestedDirectoryColumnKey ?? "",
                    out _,
                    out message);
            }
            MessageBox.Show(message, "批量打印设置", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            // 每次 CAD 取样后重新打开设置页，支持连续调整目录行高和多个列宽。
        }
    }

    [CommandMethod("_ZBP_INTERNAL_SETTINGS", CommandFlags.Session)]
    public void ShowSettingsLegacy() => ShowSettings();

    [CommandMethod("ZBP_SHORTCUT_SETTINGS", CommandFlags.Session)]
    public void ShortcutSettings() => ShortcutSettingsCore();

    [CommandMethod("_ZBP_INTERNAL_SHORTCUT_SETTINGS", CommandFlags.Session)]
    public void ShortcutSettingsLegacy() => ShortcutSettingsCore();


    [CommandMethod("ZBP_ABOUT")]
    public void About()
    {
        using var dialog = new AboutDialog();
        ShowModalDialog(dialog);
    }

    [CommandMethod("_ZBP_INTERNAL_ABOUT")]
    public void AboutLegacy() => About();

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

    // ---- 快捷键设置 ----

    private static void ShortcutSettingsCore()
    {
        var settings = AppSettingsStore.Load();
        using var form = new ShortcutSettingsDialog(settings.CommandAliases);
        if (ShowModalDialog(form) != DialogResult.OK)
        {
            return;
        }

        settings.CommandAliases = CommandAliasManager.NormalizeAliases(
            form.Aliases.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        AppSettingsStore.Save(settings);

        var applied = CommandAliasManager.Apply(settings.CommandAliases, out var message);
        MessageBox.Show(
            message,
            "快捷键设置",
            MessageBoxButtons.OK,
            applied ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

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

    /// <summary>
    /// 共享的"选择扫描范围"对话框，供图框块打印和矩形框打印共用。
    /// </summary>
    internal static TitleBlockScanScope? PromptScanScope(IWin32Window? owner)
    {
        using var form = new Form
        {
            Text = "扫描当前图",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(UiLayout.Scale(360), UiLayout.Scale(220))
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(UiLayout.Scale(16), UiLayout.Scale(12), UiLayout.Scale(16), UiLayout.Scale(12))
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(28)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label { Text = "选择扫描范围", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        var all = new RadioButton { Text = "扫描本图全部模型和布局", Dock = DockStyle.Fill, Checked = true };
        var layouts = new RadioButton { Text = "扫描全部布局", Dock = DockStyle.Fill };
        var current = new RadioButton { Text = "扫描当前布局/模型", Dock = DockStyle.Fill };
        var model = new RadioButton { Text = "扫描模型空间", Dock = DockStyle.Fill };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(10), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        var cancel = UiLayout.CreateButton("取消", 76);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(all, 0, 1);
        panel.Controls.Add(layouts, 0, 2);
        panel.Controls.Add(current, 0, 3);
        panel.Controls.Add(model, 0, 4);
        panel.Controls.Add(buttons, 0, 5);
        form.Controls.Add(panel);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(owner) != DialogResult.OK) return null;
        if (all.Checked) return TitleBlockScanScope.AllSpaces;
        if (layouts.Checked) return TitleBlockScanScope.PaperLayouts;
        if (current.Checked) return TitleBlockScanScope.CurrentSpace;
        return TitleBlockScanScope.ModelSpace;
    }

    internal static bool TryGetRegion(Editor editor, string firstPrompt, string secondPrompt, Matrix3d inverseBlockTransform, out LocalRectangle region)
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

    /// <summary>
    /// 动态加长图框的标题栏通常跟随右边界移动；横向以外框右边、纵向以外框下边存储相对坐标。
    /// </summary>
    private static LocalRectangle ToFrameRightBottomRelative(LocalRectangle region, LocalRectangle referenceFrame)
    {
        return LocalRectangle.FromPoints(
            region.MinX - referenceFrame.MaxX,
            region.MinY - referenceFrame.MinY,
            region.MaxX - referenceFrame.MaxX,
            region.MaxY - referenceFrame.MinY);
    }

    private static bool IsGenericDynamicPaperName(string paperName)
    {
        return !string.IsNullOrWhiteSpace(paperName)
               && paperName.EndsWith("+", StringComparison.Ordinal);
    }

    private static string GetGenericDynamicPaperBaseName(string paperName)
    {
        return IsGenericDynamicPaperName(paperName)
            ? paperName.Substring(0, paperName.Length - 1)
            : "";
    }

    private static void AddBlockLog(string message)
    {
        if (!BatchPlotLogger.IsEnabled)
        {
            return;
        }

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
