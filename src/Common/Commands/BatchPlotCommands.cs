using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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

    // WPF pack URI 资源查找依赖按短名定位插件程序集；命令类首次被使用前必须挂好兜底解析。
    static BatchPlotCommands()
    {
        LoadedAssemblyResolver.Register();
    }

    // ---- 插件生命周期 ----

    public void Initialize()
    {
        LoadedAssemblyResolver.Register();

        if (IsCoreConsole())
        {
            return;
        }

#if ACAD_CORE
        PluginAssemblyResolver.Register();
#endif

        var pdfInstall = AcadPlotterInstaller.InstallBundledPlotter();
        // 新用户首次加载时生成软件自有的栅格绘图器，并按需刷新当前 CAD 会话的设备缓存。
        var pngInstall = AcadPlotterInstaller.InstallPngPlotter();
        var jpgInstall = AcadPlotterInstaller.InstallJpgPlotter();
        AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(
            pdfInstall.Written || pngInstall.Written || jpgInstall.Written);
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

    /**
     * 非模态窗预览入口：由 SendStringToExecute 拉起，确保 PlotEngine 在文档命令上下文中启动。
     * 必须保持文档上下文（不要加 Session），否则预览窗会「假启动」、滚轮仍归主编辑器。
     * 外部图须先在应用上下文 PrepareJobDocument，再对本命令投递；此处禁止 Open/切文档。
     * NoHistory：不污染用户命令历史。
     */
    [CommandMethod("_ZBP_INTERNAL_PREVIEW", CommandFlags.NoHistory)]
    public void InternalPreview()
    {
        var request = PendingPlotPreview.Take();
        if (request == null)
        {
            return;
        }

        try
        {
            PlotterService.Preview(request.Job, request.DeviceName, request.StyleSheet, request.Document);
        }
        catch (System.Exception ex)
        {
            request.OnError?.Invoke(ex);
        }
        finally
        {
            request.OnFinally?.Invoke();
        }
    }

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
        var form = new TitleBlockLibraryManagerForm();
        CadDialog.ShowModal(form);
    }

    [CommandMethod("_ZBP_INTERNAL_MANAGE_LIBRARY", CommandFlags.Session)]
    public void ManageLibraryLegacy() => ManageLibrary();

    [CommandMethod("ZBP_SETTINGS", CommandFlags.Session)]
    public void ShowSettings()
    {
        while (true)
        {
            var form = new SettingsForm();
            if (CadDialog.ShowModal(form) != true)
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
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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

            // 成功后设置页会重开并显示新值，不必再弹“已设置”提示；失败/取消仍提示。
            if (!ok)
            {
                MessageBox.Show(message, "批量打印设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
        var dialog = new AboutDialog();
        CadDialog.ShowModal(dialog);
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
                "已安装自动加载。\n\n仅对当前这套CAD生效，其它版本不会自动加载。\n下次启动当前CAD后会自动加载批量打印插件。\n\n写入位置:\n" + string.Join("\n", roots),
                "批量打印",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("安装自动加载失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    ? "已卸载自动加载。\n\n仅取消了当前这套CAD的自动加载。当前已加载的插件会在本次会话继续可用，关闭CAD后不会再自动加载。"
                    : "没有找到已安装的自动加载项。",
                "批量打印",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show("卸载自动加载失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var form = new ShortcutSettingsDialog(settings.CommandAliases);
        if (CadDialog.ShowModal(form) != true)
        {
            return;
        }

        settings.CommandAliases = CommandAliasManager.NormalizeAliases(
            form.Aliases.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        try
        {
            AppSettingsStore.Save(settings);
        }
        catch (System.Exception ex)
        {
            // 快捷键未能保存时不应用到当前会话，避免重启后与界面显示不一致。
            MessageBox.Show(
                AppSettingsStore.FormatSaveError(ex),
                "快捷键设置",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var applied = CommandAliasManager.Apply(settings.CommandAliases, out var message);
        MessageBox.Show(
            message,
            "快捷键设置",
            MessageBoxButton.OK,
            applied ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    // ---- 批量打印面板（图框库匹配） ----

    private static void ShowBatchPlotWindowCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_batchPlotForm is { IsLoaded: true })
        {
            _batchPlotForm.Activate();
            return;
        }

        var form = new BatchPlotForm(doc);
        _batchPlotForm = form;
        form.Closed += (_, _) =>
        {
            if (ReferenceEquals(_batchPlotForm, form))
            {
                _batchPlotForm = null;
            }
        };

        ShowModelessDialog(form);
    }

    // ---- 通用型批量打印 ----

    private static void ShowRectangleBatchPlotCore()
    {
        var doc = CadApp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            return;
        }

        if (_rectangleBatchPlotForm is { IsLoaded: true })
        {
            _rectangleBatchPlotForm.Activate();
            return;
        }

        var form = new RectangleBatchPlotForm(doc);
        _rectangleBatchPlotForm = form;
        form.Closed += (_, _) =>
        {
            if (ReferenceEquals(_rectangleBatchPlotForm, form))
            {
                _rectangleBatchPlotForm = null;
            }
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

    private static System.Windows.Forms.DialogResult ShowModalDialog(System.Windows.Forms.Form form)
    {
#if ACAD_CORE
        var result = form.ShowDialog();
#else
        var result = CadApp.ShowModalDialog(form);
#endif
        // 顶层 WinForms 模态结束后交还 CAD 焦点，与 CadDialog WPF 路径一致。
        CadWindowFocus.ActivateCadWindow();
        return result;
    }

    /// <summary>
    /// 非模态显示 WinForms 对话框并等到关闭：CAD 仍可缩放/平移，便于核对红色临时框。
    /// </summary>
    private static System.Windows.Forms.DialogResult ShowModelessDialogAndWait(System.Windows.Forms.Form form)
    {
        form.TopMost = true;
#if ACAD_CORE
        form.Show();
#else
        CadApp.ShowModelessDialog(form);
#endif
        while (form.Visible)
        {
            System.Windows.Forms.Application.DoEvents();
            System.Threading.Thread.Sleep(15);
        }

        CadWindowFocus.ActivateCadWindow();
        return form.DialogResult;
    }

    private static bool? ShowModalDialog(Window window)
    {
        return CadDialog.ShowModal(window);
    }

    private static void ShowModelessDialog(Window window)
    {
        CadDialog.ShowModeless(window);
    }

    /// <summary>
    /// 共享「选择扫描范围」对话框，供图框块打印和通用型批量打印共用。
    /// 四个范围各一个按钮，点击即返回对应范围；关闭窗口即取消。
    /// <paramref name="owner"/> 应为批打主窗，关闭后焦点回到批打界面。
    /// </summary>
    internal static TitleBlockScanScope? PromptScanScope(Window? owner = null)
    {
        TitleBlockScanScope? chosen = null;
        var dialog = new Window
        {
            Title = "扫描当前图",
            Width = 360,
            MinWidth = 340,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.ToolWindow,
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
            FontSize = 11
        };

        var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };

        void AddScopeButton(string content, TitleBlockScanScope scope)
        {
            var button = new Button
            {
                Content = content,
                MinHeight = 28,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4)
            };
            try
            {
                if (System.Windows.Application.Current?.TryFindResource("PluginButtonStyle") is Style style)
                {
                    button.Style = style;
                }
            }
            catch
            {
            }

            button.Click += (_, _) =>
            {
                chosen = scope;
                dialog.DialogResult = true;
                dialog.Close();
            };
            panel.Children.Add(button);
        }

        AddScopeButton("扫描本图全部模型和布局", TitleBlockScanScope.AllSpaces);
        AddScopeButton("扫描全部布局", TitleBlockScanScope.PaperLayouts);
        AddScopeButton("扫描当前布局/模型", TitleBlockScanScope.CurrentSpace);
        AddScopeButton("扫描模型空间", TitleBlockScanScope.ModelSpace);

        // 最后一项去掉多余底边距
        if (panel.Children[panel.Children.Count - 1] is FrameworkElement last)
        {
            last.Margin = new Thickness(0);
        }

        dialog.Content = panel;

        var result = CadDialog.ShowModal(dialog, owner);
        owner?.Activate();
        if (result != true)
        {
            return null;
        }

        return chosen;
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
        => TitleBlockRegionConverter.ToFrameRelative(region, referenceFrame);

    /// <summary>
    /// 动态加长图框的标题栏通常跟随右边界移动；横向以外框右边、纵向以外框下边存储相对坐标。
    /// </summary>
    private static LocalRectangle ToFrameRightBottomRelative(LocalRectangle region, LocalRectangle referenceFrame)
        => TitleBlockRegionConverter.ToFrameRightBottomRelative(region, referenceFrame);

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
