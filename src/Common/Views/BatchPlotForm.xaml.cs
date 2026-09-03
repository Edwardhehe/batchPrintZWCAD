using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
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
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

/// <summary>
/// 图框块批量打印面板（WPF 版，非模态窗口；由 BatchPlotCommands 通过 CadDialog.ShowModeless 显示）。
/// </summary>
public sealed partial class BatchPlotForm : Window
{
    private readonly Document _currentDocument;
    private readonly BindingList<PlotJob> _jobs = new();
    private CancellationTokenSource? _printCts;
    private readonly List<string> _logLines = new();
    private readonly List<string> _selectedDwgFiles = new();
    private readonly TemporarySequenceOverlay _sequenceOverlay;
    private readonly AppSettings _settings;
    private bool _sequenceOverlayFollowsCurrentJobs;
    private int _overlayScheduleGeneration;
    private bool _outputDirectoryIsCustom;
    private bool _outputDirectoryModified;
    private bool _updatingPrintSelection;
    private bool _allowDoubleClickTextEdit;
    private bool _uiReady;
    private bool _closed;
    private List<PlotJob>? _pendingPrintToggleJobs;
    private DrawingNumberReorderDialog? _renumberDialog;
    private Dictionary<PlotJob, string>? _renumberOriginalNumbers;
    private List<PlotJob>? _renumberCurrentJobs;
    private PlotJob? _highlightedJob;
    private string _lastLogPath = "";
    private string _mergedOutputPath = "";
    private string _pngPlotDevice = "";
    private string _jpgPlotDevice = "";
    private string _dwfPlotDevice = "";
    private long _nextSortPriority;
    private HashSet<string> _duplicateDrawingNumbers = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _duplicateTitles = new(StringComparer.OrdinalIgnoreCase);
    private bool _styleSelectionReady;

    /// <summary>原 _plotOnlyControls：非 DWG 输出格式时才启用。</summary>
    private readonly UIElement[] _plotOnlyControls = default!;

    public bool HasPendingPrint { get; private set; }

    public BatchPlotForm(Document currentDocument)
    {
        _currentDocument = currentDocument;
        _sequenceOverlay = new TemporarySequenceOverlay(currentDocument);
        _settings = AppSettingsStore.Load();
        InitializeComponent();

        _plotOnlyControls = new UIElement[]
        {
            _styleCombo,
            _styleSettingsButton,
            _leaveMarginCheckBox,
            _marginInput
        };

        // 首次使用默认为关闭；之后恢复用户上一次操作，避免每次重复勾选。
        _mergePdfCheckBox.IsChecked = _settings.MergePdf;
        _leaveMarginCheckBox.IsChecked = _settings.LeavePaperMargin;
        InitMarginCombo(_marginInput, 68, _settings.PaperMarginMm);
        _marginInput.IsEnabled = _leaveMarginCheckBox.IsChecked == true;
        _outputDirectory.Text = GetDefaultOutputDirectory();

        _grid.ItemsSource = _jobs;

        LoadPlotOptions();
        RefreshStatus();
        _uiReady = true;

        Closing += (_, _) =>
        {
            if (!HasPendingPrint)
            {
                _renumberDialog?.Close();
                ClearSequenceOverlay(repaint: false);
            }

            SaveCurrentSettings();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            // 关闭窗口时只清理临时红框和序号，不再退订或接管 CAD 删除命令。
            _sequenceOverlay.Dispose();
        };
    }

    // ── 初始化 ──

    private void LoadPlotOptions()
    {
        _outputFormatCombo.Items.Clear();
        _outputFormatCombo.Items.Add("PDF");
        _outputFormatCombo.Items.Add("PNG");
        _outputFormatCombo.Items.Add("JPG");
        _outputFormatCombo.Items.Add("DWF");
        _outputFormatCombo.Items.Add("DWG");
        _outputFormatCombo.SelectedIndex = 0;
        RefreshSavePathModeOptions(preserveSelection: false);

        try
        {
            var pdfInstall = AcadPlotterInstaller.InstallBundledPlotter();
            var pngInstall = AcadPlotterInstaller.InstallPngPlotter();
            var jpgInstall = AcadPlotterInstaller.InstallJpgPlotter();
            var dwfInstall = AcadPlotterInstaller.InstallDwfPlotter();
            AcadPlotterInstaller.RefreshPlotterDevicesIfNeeded(
                pdfInstall.Written || pngInstall.Written || jpgInstall.Written || dwfInstall.Written);
            var validator = PlotSettingsValidator.Current;
            var devices = validator.GetPlotDeviceList()
                .Cast<object>()
                .Select(item => item?.ToString() ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            _pngPlotDevice = FindPngPlotDevice(devices, pngInstall.DeviceName);
            _jpgPlotDevice = FindJpgPlotDevice(devices, jpgInstall.DeviceName);
            _dwfPlotDevice = FindDwfPlotDevice(devices, dwfInstall.DeviceName);
            foreach (var style in PlotStyleManager.GetAvailableCtbStyles())
            {
                _styleCombo.Items.Add(style);
            }

            PlotStyleManager.RestoreSavedStyle(_styleCombo, _settings.LastStyleSheet);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("读取 CTB 失败: " + ex.Message, "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        UpdateOutputFormatUi();
        SaveCurrentSettings();
        _styleSelectionReady = true;
    }

    private static string FindPngPlotDevice(IReadOnlyList<string> devices, string installedPlotter)
    {
        var preferred = new[]
        {
            installedPlotter,
            AcadPlotterInstaller.PreferredPngPlotter
        };
        foreach (var expected in preferred.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = devices.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return "";
    }

    private static string FindJpgPlotDevice(IReadOnlyList<string> devices, string installedPlotter)
    {
        var preferred = new[]
        {
            installedPlotter,
            AcadPlotterInstaller.PreferredJpgPlotter
        };
        foreach (var expected in preferred.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = devices.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return "";
    }

    private static string FindDwfPlotDevice(IReadOnlyList<string> devices, string installedPlotter)
    {
        var preferred = new[]
        {
            installedPlotter,
            AcadPlotterInstaller.PreferredDwfPlotter,
            "DWF6 ePlot.pc3",
            "DWF6 ePlot.pc5",
            "ZWPLOT_DWF.pc5",
            "M_DWF.pc5"
        };
        foreach (var expected in preferred.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = devices.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return devices.FirstOrDefault(value => value.IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                                               && value.IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0)
               ?? installedPlotter;
    }

    // ── 扫描 ──

    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope();

    private void ScanCurrentDrawing()
    {
        var library = TitleBlockLibraryStore.Load();
        if (library.Blocks.Count == 0)
        {
            System.Windows.MessageBox.Show("图框库为空。请先从“批量打印”菜单点击“新增图框”。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            ClearSequenceOverlay();
            RefreshStatus();
            return;
        }

        var scope = PromptScanScope();
        if (scope == null)
        {
            return;
        }

        _selectedDwgFiles.Clear();
        var scannedJobs = TitleBlockScanner.Scan(
            _currentDocument,
            library,
            scope.Value,
            _settings.PaperMatchToleranceMm);

        // 扫描结果坐标是 WCS，转为 DCS 后打印（和矩形框批量打印同理）
        TransformScannedJobsToDcs(scannedJobs);
        SortAndRefreshOutputPaths(scannedJobs);
        ScheduleSequenceOverlayForCurrentJobs();
        AppendLog("INFO", $"扫描当前图完成，识别 {_jobs.Count} 张。");
    }

    private void ScanSelectedWindow()
    {
        var library = TitleBlockLibraryStore.Load();
        if (library.Blocks.Count == 0)
        {
            System.Windows.MessageBox.Show("图框库为空，请先新增图框。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            ClearSequenceOverlay();
            return;
        }

        CadWindowFocus.HideForCadInput(this);
        try
        {
            var editor = _currentDocument.Editor;
            var first = editor.GetPoint(new PromptPointOptions("\n框选扫描范围第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = editor.GetCorner(new PromptCornerOptions("\n框选扫描范围对角点: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            // 保留用户框选时的 UCS 矩形和基轴。旋转 UCS 不能先压成 WCS 包围盒，
            // 否则扫描/打印阶段再次取 DCS 包围盒时范围会被放大。
            var window = CadCoordinateSystem.CreateSelectionWindow(
                editor,
                first.Value,
                second.Value,
                _currentDocument.Database.TileMode);

            _selectedDwgFiles.Clear();
            var scannedJobs = TitleBlockScanner.Scan(
                _currentDocument,
                library,
                window,
                _settings.PaperMatchToleranceMm);

            // 扫描结果坐标是 WCS，转为 DCS 后打印
            TransformScannedJobsToDcs(scannedJobs);
            SortAndRefreshOutputPaths(scannedJobs);
            ScheduleSequenceOverlayForCurrentJobs();
            AppendLog("INFO", $"框选扫描当前图完成，识别 {_jobs.Count} 张。");
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    /// <summary>扫描得到的 Job 坐标是 WCS，转换为 DCS 后打印（和矩形框批量打印同理）。</summary>
    private void TransformScannedJobsToDcs(List<PlotJob> jobs)
    {
        try
        {
            var wcsToDcs = BatchPlotCommands.BuildWcsToDcsMatrix(_currentDocument.Editor);
            foreach (var job in jobs)
            {
                if (job.UsesUserCoordinateSystem)
                {
                    // UCS 任务保留 WCS 边界和 UCS 元数据；打印阶段先把视图对齐到该 UCS，
                    // 再由真实四角一次性生成 DCS 窗口。
                    job.IsDcsWindow = false;
                    job.IsManualWindow = true;
                    continue;
                }

                // 和图框块扫描一样的四点法：4 个 WCS 角点 × WCS→DCS → 取一次包围盒
                // 优先用 CornerPoints（图框库参考框的实际 WCS 角点，避免包围盒二次放大）
                // 兜底用 Min/Max（老版图框库数据或无 PrintRegion 的块）
                Point3d[] pts;
                var cp = job.CornerPoints;
                if (cp != null)
                {
                    pts = new[]
                    {
                        new Point3d(cp[0], cp[1], 0).TransformBy(wcsToDcs),
                        new Point3d(cp[2], cp[3], 0).TransformBy(wcsToDcs),
                        new Point3d(cp[4], cp[5], 0).TransformBy(wcsToDcs),
                        new Point3d(cp[6], cp[7], 0).TransformBy(wcsToDcs)
                    };
                }
                else
                {
                    pts = new[]
                    {
                        new Point3d(job.MinX, job.MinY, 0).TransformBy(wcsToDcs),
                        new Point3d(job.MaxX, job.MinY, 0).TransformBy(wcsToDcs),
                        new Point3d(job.MaxX, job.MaxY, 0).TransformBy(wcsToDcs),
                        new Point3d(job.MinX, job.MaxY, 0).TransformBy(wcsToDcs)
                    };
                }
                job.MinX = pts.Min(p => p.X);
                job.MinY = pts.Min(p => p.Y);
                job.MaxX = pts.Max(p => p.X);
                job.MaxY = pts.Max(p => p.Y);
                job.IsDcsWindow = true;
                // 阻止 PlotterService 重新扫描 DWG 刷新坐标（和矩形框批量打印同理）
                job.IsManualWindow = true;
            }
        }
        catch (System.Exception ex)
        {
            AppendLog("WARN", $"图框扫描 WCS→DCS 变换失败，使用 WCS 坐标：{ex.Message}");
        }
    }

    private void AddDwgFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DWG 文件 (*.dwg)|*.dwg",
            Multiselect = true,
            Title = "选择要批量打印的 DWG"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var filesToScan = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            var fullPath = Path.GetFullPath(file);
            if (!_selectedDwgFiles.Any(x => string.Equals(x, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedDwgFiles.Add(fullPath);
                filesToScan.Add(fullPath);
            }
        }

        var library = TitleBlockLibraryStore.Load();
        var added = new List<PlotJob>();
        var errors = new List<string>();

        foreach (var file in filesToScan)
        {
            try
            {
                var scanned = ScanExternalFile(file, library);
                added.AddRange(scanned);
                AppendLog("INFO", $"扫描 {file}，识别 {scanned.Count} 张。");
            }
            catch (Exception ex)
            {
                var message = $"{file}: {ex.Message}";
                errors.Add(message);
                AppendLog("ERROR", "扫描失败，" + message);
            }
        }

        if (added.Count > 0)
        {
            SortAndRefreshOutputPaths(_jobs.Concat(added).ToList());
        }
        else
        {
            SortAndRefreshOutputPaths();
        }
        if (_jobs.Count > 0 && _jobs.All(IsCurrentDocumentJob))
        {
            ScheduleSequenceOverlayForCurrentJobs();
        }
        else
        {
            ClearSequenceOverlay();
        }

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show("部分 DWG 扫描失败:\n" + string.Join("\n", errors), "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private List<PlotJob> ScanExternalFile(string file, TitleBlockLibrary library)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(_currentDocument.Database.Filename), StringComparison.OrdinalIgnoreCase))
        {
            return TitleBlockScanner.Scan(
                _currentDocument,
                library,
                TitleBlockScanScope.AllSpaces,
                _settings.PaperMatchToleranceMm);
        }

        using var db = new Database(false, true);
        db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        return TitleBlockScanner.Scan(
            db,
            library,
            file,
            null,
            TitleBlockScanScope.AllSpaces,
            null,
            _settings.PaperMatchToleranceMm);
    }

    /// <summary>
    /// 先排序并重算文件名，再一次性绑到表格。
    /// 扫描等入口应传入新清单，避免先绑未排序结果再绑一次。
    /// CAD 红框仅在图号或打印顺序变化时整批重建。
    /// </summary>
    /// <param name="sourceJobs">新清单；为空则对当前表格数据排序后重绑。</param>
    private void SortAndRefreshOutputPaths(IReadOnlyList<PlotJob>? sourceJobs = null)
    {
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var overlayKeyBefore = CaptureOverlayRebuildKey();

        // 所有会改变清单的入口最终都回到这里，确保表格、红框序号、输出文件名和打印顺序使用同一结果。
        var sorted = SortTitleBlockJobs(sourceJobs == null ? _jobs.ToList() : sourceJobs.ToList());

        var sequenceDigits = FileNameSanitizer.ResolveSequenceDigits(
            _settings.AutoFileNameSequenceDigits,
            _settings.FileNameSequenceDigits,
            _settings.FileNameSequenceStartNumber,
            sorted.Count);
        var dwgOutputPaths = IsDwgOutput
            ? DwgSplitService.BuildOutputPaths(
                sorted,
                _currentDocument,
                _settings,
                customOutputDirectory: CustomOutputDirectory,
                sourceSubfolder: AutomaticOutputSubfolder)
            : null;
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refreshed = new List<PlotJob>(sorted.Count);
        for (var index = 0; index < sorted.Count; index++)
        {
            var job = sorted[index];
            if (dwgOutputPaths != null)
            {
                job.OutputPath = dwgOutputPaths[job];
            }
            else
            {
                var sequenceNumber = _settings.FileNameSequenceStartNumber + index;
                job.OutputPath = BuildOutputPath(job, sequenceNumber, sequenceDigits, reservedPaths);
            }
            // 表格显示最终命名结果；合并 PDF 的临时路径不得覆盖这个值。
            job.DisplayOutputFileName = job.OutputFileName;
            refreshed.Add(job);
        }

        ReplaceBindingListContents(_jobs, refreshed);
        _grid.Items.Refresh();

        RebuildDuplicateIdentitySets();
        RefreshStatus();
        if (_sequenceOverlayFollowsCurrentJobs
            && !OverlayRebuildKeysEqual(overlayKeyBefore, CaptureOverlayRebuildKey()))
        {
            ScheduleSequenceOverlayForCurrentJobs();
        }
    }

    /// <summary>
    /// 一次性替换绑定列表，结束时只发一次 Reset。
    /// 大图识别出几十上百张时，逐行 Add 会让表格反复重绑，界面卡住。
    /// </summary>
    private static void ReplaceBindingListContents<T>(BindingList<T> target, IEnumerable<T> values)
    {
        target.RaiseListChangedEvents = false;
        try
        {
            target.Clear();
            foreach (var value in values)
            {
                target.Add(value);
            }
        }
        finally
        {
            target.RaiseListChangedEvents = true;
            target.ResetBindings();
        }
    }

    // ── 表格事件 ──

    private void GridCellMouseDownLeft(PlotJob clickedJob)
    {
        if (_grid.SelectedItems.Count > 1)
        {
            // 先记住点击前的多选行；DataGrid 点击复选框时可能会先改当前选择，后续统一同步这些行。
            var highlightedJobs = GetHighlightedJobs();
            _pendingPrintToggleJobs = highlightedJobs.Contains(clickedJob) ? highlightedJobs : null;
        }
        else
        {
            _pendingPrintToggleJobs = null;
        }
    }

    private static DataGridRow? HitTestRow(DependencyObject? source)
    {
        while (source != null && source is not DataGridRow)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return source as DataGridRow;
    }

    private static DataGridCell? HitTestCell(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is DataGridCell cell)
            {
                return cell;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = HitTestRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not PlotJob clickedJob)
        {
            return;
        }

        var cell = HitTestCell(e.OriginalSource as DependencyObject);
        if (cell?.Column == _printColumn)
        {
            // 点击“打印”勾选框前先记住多选行（原 CellMouseDown 逻辑）。
            GridCellMouseDownLeft(clickedJob);
        }
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = HitTestRow(e.OriginalSource as DependencyObject);
        if (row?.Item is not PlotJob job)
        {
            return;
        }

        if (!_grid.SelectedItems.Contains(job))
        {
            _grid.UnselectAll();
            _grid.SelectedItem = job;
        }

        _grid.CurrentCell = new DataGridCellInfo(job, _grid.CurrentColumn ?? _grid.Columns[0]);
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_grid.CurrentItem is PlotJob job)
        {
            // 换行只切换已有标注的高亮属性，避免整批删除重画导致 CAD 卡顿。
            HighlightSequenceOverlayJob(job);
        }
    }

    private void MoveCurrentJobToFirst()
    {
        if (_settings.TitleBlockBatchSortMode == TitleBlockSortMode.Spatial)
        {
            return;
        }

        if (_grid.CurrentItem is not PlotJob job)
        {
            return;
        }

        job.SortPriority = ++_nextSortPriority;
        SortAndRefreshOutputPaths();
        _grid.SelectedItem = job;
        if (_grid.Columns.Count > 0)
        {
            _grid.CurrentCell = new DataGridCellInfo(job, _grid.Columns[0]);
        }
        _grid.ScrollIntoView(job);
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var enabled = _grid.CurrentItem is PlotJob;
        // 纯位置模式不允许任何手工优先级介入，菜单直接禁用，避免点击后看似成功但顺序不变。
        _moveToFirstItem.IsEnabled = enabled
                                     && _settings.TitleBlockBatchSortMode != TitleBlockSortMode.Spatial;
        _markNotPrintItem.IsEnabled = enabled;
        _deleteItem.IsEnabled = enabled;
        if (!enabled)
        {
            e.Handled = true;
        }
    }

    private void MoveToFirst_Click(object sender, RoutedEventArgs e) => MoveCurrentJobToFirst();

    private void MarkNotPrint_Click(object sender, RoutedEventArgs e) => MarkHighlightedJobsNotPrint();

    private void DeleteHighlighted_Click(object sender, RoutedEventArgs e) => RemoveHighlightedJobs();

    private void MarkHighlightedJobsNotPrint()
    {
        foreach (var job in GetHighlightedJobs())
        {
            job.Selected = false;
        }

        // 右键“不打印”与取消勾选行为一致：直接从清单移除并重新编号。
        RemoveUnselectedJobs();
    }

    private List<PlotJob> GetHighlightedJobs()
    {
        var jobs = _grid.SelectedItems.OfType<PlotJob>().Distinct().ToList();
        if (jobs.Count == 0 && _grid.CurrentItem is PlotJob current)
        {
            jobs.Add(current);
        }

        return jobs;
    }

    private void RemoveHighlightedJobs()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var highlightedJobs = GetHighlightedJobs();

        if (highlightedJobs.Count == 0)
        {
            System.Windows.MessageBox.Show("没有高亮选中的图纸行。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var job in highlightedJobs)
        {
            _jobs.Remove(job);
        }

        SortAndRefreshOutputPaths();
        RefreshSelectedOverlay();
    }

    private void PrintCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingPrintSelection)
        {
            return;
        }

        if (((CheckBox)sender).DataContext is not PlotJob changedJob)
        {
            return;
        }

        ApplyPrintSelectionToHighlightedRows(changedJob);
        if (!changedJob.Selected)
        {
            RemoveUnselectedJobs();
            return;
        }

        RefreshStatus();
        RefreshSelectedOverlay();
    }

    // 图框块界面取消“打印”即表示从当前清单移除，避免列表编号和 CAD 红框编号不一致（与矩形框批量打印同理）。
    private void RemoveUnselectedJobs()
    {
        var removed = _jobs.Where(job => !job.Selected).ToList();
        if (removed.Count == 0)
        {
            _grid.Items.Refresh();
            return;
        }

        foreach (var job in removed)
        {
            if (ReferenceEquals(_highlightedJob, job))
            {
                _highlightedJob = null;
            }
            _jobs.Remove(job);
        }

        // 移除后重新排序、编号并刷新覆盖层，保持表格与 CAD 红框一致。
        SortAndRefreshOutputPaths();
    }

    private void ApplyPrintSelectionToHighlightedRows(PlotJob changedJob)
    {
        var targetJobs = _pendingPrintToggleJobs ?? GetHighlightedJobs();
        _pendingPrintToggleJobs = null;
        if (targetJobs.Count <= 1 || !targetJobs.Contains(changedJob))
        {
            return;
        }

        try
        {
            _updatingPrintSelection = true;
            // 多行高亮后点击“打印”勾选框时，以当前行状态为准批量同步，支持 Shift/Ctrl 选中后一次切换。
            foreach (var job in targetJobs)
            {
                job.Selected = changedJob.Selected;
            }
        }
        finally
        {
            _updatingPrintSelection = false;
        }

        _grid.Items.Refresh();
    }

    private void RefreshSelectedOverlay()
    {
        if (_sequenceOverlayFollowsCurrentJobs)
        {
            ScheduleSequenceOverlayForCurrentJobs();
        }
    }

    private void Grid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        // 图号、图名只能由双击显式进入编辑；单击只负责选中/高亮，避免误触后直接改字。
        if (IsDrawingIdentityColumn(e.Column) && !_allowDoubleClickTextEdit)
        {
            e.Cancel = true;
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = HitTestCell(e.OriginalSource as DependencyObject);
        if (cell?.Column == null || cell.Column != _drawingNumberColumn && cell.Column != _titleColumn)
        {
            return;
        }

        try
        {
            _allowDoubleClickTextEdit = true;
            _grid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
            _grid.BeginEdit();
        }
        finally
        {
            // BeginningEdit 在 BeginEdit 内同步触发，离开后立即收回授权，后续单击仍不能进入编辑。
            _allowDoubleClickTextEdit = false;
        }
    }

    private void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row?.Item is not PlotJob job)
        {
            return;
        }

        var titleChanged = e.Column == _titleColumn && !string.Equals(job.Title, job.CadTitle, StringComparison.Ordinal);
        var numberChanged = e.Column == _drawingNumberColumn && !string.Equals(job.DrawingNumber, job.CadDrawingNumber, StringComparison.Ordinal);
        if (!titleChanged && !numberChanged)
        {
            return;
        }

        var ok = CadTextUpdater.TryUpdateOpenDocument(
            job,
            titleChanged ? job.Title : null,
            numberChanged ? job.DrawingNumber : null,
            _currentDocument,
            out var message);

        if (ok)
        {
            if (titleChanged)
            {
                job.CadTitle = job.Title;
            }

            if (numberChanged)
            {
                job.CadDrawingNumber = job.DrawingNumber;
            }
        }

        AppendLog(ok ? "INFO" : "WARN", message);
        if (numberChanged)
        {
            // 手工修改图号后，列表编号、实际打印顺序和 CAD 红框顺序都要按新图号刷新。
            job.SortPriority = 0;
        }
        SortAndRefreshOutputPaths();
    }

    private bool IsDrawingIdentityColumn(DataGridColumn? column)
    {
        return column == _drawingNumberColumn || column == _titleColumn;
    }

    /// <summary>
    /// 根据当前清单重建重复图号、图名集合，供表格把重复项标红。
    /// 空白图号/图名不参与检查，避免未识别字段全部被当成重复。
    /// </summary>
    private void RebuildDuplicateIdentitySets()
    {
        _duplicateDrawingNumbers = FindDuplicateIdentityKeys(_jobs.Select(job => job.DrawingNumber));
        _duplicateTitles = FindDuplicateIdentityKeys(_jobs.Select(job => job.Title));
        DuplicateBrushConverter.DrawingNumbers = _duplicateDrawingNumbers;
        DuplicateBrushConverter.Titles = _duplicateTitles;
        _grid.Items.Refresh();
    }

    /// <summary>
    /// 找出出现超过一次的图号或图名（忽略大小写和首尾空白）。
    /// </summary>
    private static HashSet<string> FindDuplicateIdentityKeys(IEnumerable<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = NormalizeIdentityText(value);
            if (key.Length == 0)
            {
                continue;
            }

            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        return new HashSet<string>(
            counts.Where(pair => pair.Value > 1).Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeIdentityText(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    // ── CAD 红框序号标注 ──

    private void ShowSequenceOverlayForCurrentJobs()
    {
        _sequenceOverlayFollowsCurrentJobs = true;
        try
        {
            var currentJobs = _jobs.Where(job => job.Selected && IsCurrentDocumentJob(job)).ToList();
            var highlightJob = _highlightedJob != null && currentJobs.Contains(_highlightedJob) ? _highlightedJob : null;
            _sequenceOverlay.Show(currentJobs, highlightJob);
        }
        catch (Exception ex)
        {
            _sequenceOverlayFollowsCurrentJobs = false;
            _sequenceOverlay.Clear();
            AppendLog("WARN", "临时序号标注显示失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 清单刷新完成后再绘制 CAD 红框，避免识别与 Regen 挤在同一次 UI 消息里。
    /// 仅扫描、图号变化或打印顺序变化时调用；改图名或重复标红不要走这里。
    /// </summary>
    private void ScheduleSequenceOverlayForCurrentJobs()
    {
        if (_closed)
        {
            return;
        }

        var generation = ++_overlayScheduleGeneration;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closed || generation != _overlayScheduleGeneration)
            {
                return;
            }

            ShowSequenceOverlayForCurrentJobs();
        }));
    }

    private void ShowRenumberPreviewOverlay(IReadOnlyList<PlotJob> previewOrder)
    {
        _sequenceOverlayFollowsCurrentJobs = true;
        try
        {
            var currentJobs = previewOrder
                .Where(job => job.Selected && IsCurrentDocumentJob(job))
                .ToList();
            var highlightJob = _highlightedJob != null && currentJobs.Contains(_highlightedJob) ? _highlightedJob : null;
            // 图号重排预览阶段，红框文字临时显示预计写入的新图号；窗口关闭后恢复为打印顺序数字。
            _sequenceOverlay.Show(currentJobs, highlightJob, (job, _) => job.DrawingNumber);
        }
        catch (Exception ex)
        {
            _sequenceOverlayFollowsCurrentJobs = false;
            _sequenceOverlay.Clear();
            AppendLog("WARN", "图号重排预览标注显示失败: " + ex.Message);
        }
    }

    private void HighlightSequenceOverlayJob(PlotJob job)
    {
        _highlightedJob = job;
        if (!_sequenceOverlayFollowsCurrentJobs)
        {
            return;
        }

        if (!job.Selected || !IsCurrentDocumentJob(job))
        {
            // 当前行不在临时标注集合里时，只记录选择，避免传入不存在的实体导致无效刷新。
            _sequenceOverlay.SetHighlight(null);
            return;
        }

        _sequenceOverlay.SetHighlight(job);
    }

    private void ClearSequenceOverlay(bool repaint = true)
    {
        _overlayScheduleGeneration++;
        _sequenceOverlayFollowsCurrentJobs = false;
        _sequenceOverlay.Clear(repaint);
    }

    private bool IsCurrentDocumentJob(PlotJob job)
    {
        var source = job.SourceFile;
        var file = _currentDocument.Database.Filename;
        if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(file))
        {
            try
            {
                return string.Equals(Path.GetFullPath(source), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(source, file, StringComparison.OrdinalIgnoreCase);
            }
        }

        return string.Equals(source, _currentDocument.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 当前会画到 CAD 上的清单快照：顺序代表打印序号，图号用于判断是否要整批重建红框。
    /// </summary>
    private List<(PlotJob Job, string DrawingNumber)> CaptureOverlayRebuildKey()
    {
        var keys = new List<(PlotJob Job, string DrawingNumber)>();
        foreach (var job in _jobs)
        {
            if (!job.Selected || !IsCurrentDocumentJob(job))
            {
                continue;
            }

            keys.Add((job, job.DrawingNumber ?? ""));
        }

        return keys;
    }

    /// <summary>
    /// 打印顺序（同一 Job 引用的先后）或图号任一变化，才需要整批 Show 红框。
    /// </summary>
    private static bool OverlayRebuildKeysEqual(
        IReadOnlyList<(PlotJob Job, string DrawingNumber)> left,
        IReadOnlyList<(PlotJob Job, string DrawingNumber)> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!ReferenceEquals(left[i].Job, right[i].Job)
                || !string.Equals(left[i].DrawingNumber, right[i].DrawingNumber, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // ── 图号重排 ──

    private void RenumberDrawingNumbers()
    {
        if (_jobs.Count == 0) return;
        if (_renumberDialog != null)
        {
            _renumberDialog.Activate();
            return;
        }

        // 仅对当前文档的图框重排
        var currentJobs = _jobs.Where(j => IsCurrentDocumentJob(j)).ToList();
        if (currentJobs.Count == 0)
        {
            System.Windows.MessageBox.Show("当前没有本图文档的图框可重排。", "图号重排", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 保存原始图号，非模态窗口取消/关闭时恢复。
        _renumberCurrentJobs = currentJobs;
        _renumberOriginalNumbers = currentJobs.ToDictionary(j => j, j => j.DrawingNumber);
        var detectedPrefix = DetectCommonDrawingNumberPrefix(currentJobs);
        _renumberDialog = new DrawingNumberReorderDialog(
            currentJobs.Count,
            detectedPrefix,
            _settings.SortOrderHorizontalFirst);
        _renumberDialog.PreviewRequested += PreviewRenumberDrawingNumbers;
        _renumberDialog.Closed += RenumberDialogClosed;
        _renumberDialog.Show();
        _renumberDialog.Activate();
    }

    private void PreviewRenumberDrawingNumbers()
    {
        if (_renumberDialog == null || _renumberCurrentJobs == null)
        {
            return;
        }

        var sorted = SortRenumberJobsByLayout(_renumberCurrentJobs, _renumberDialog.HorizontalFirst);
        ApplyRenumbering(sorted, _renumberDialog.Prefix, _renumberDialog.Suffix, _renumberDialog.StartNumber, _renumberDialog.Digits);
        RebuildDuplicateIdentitySets();
        _grid.Items.Refresh();
        ShowRenumberPreviewOverlay(sorted);
    }

    private void RenumberDialogClosed(object? sender, EventArgs e)
    {
        var dialog = (DrawingNumberReorderDialog)sender!;
        var currentJobs = _renumberCurrentJobs ?? new List<PlotJob>();
        var originalNumbers = _renumberOriginalNumbers ?? new Dictionary<PlotJob, string>();
        _renumberDialog = null;
        _renumberCurrentJobs = null;
        _renumberOriginalNumbers = null;

        if (dialog.DialogResult != true)
        {
            // 恢复原始图号，并把 CAD 红框恢复为打印顺序数字。
            foreach (var kv in originalNumbers)
            {
                kv.Key.DrawingNumber = kv.Value;
            }
            _grid.Items.Refresh();
            SortAndRefreshOutputPaths();
            return;
        }

        var finalSorted = SortRenumberJobsByLayout(currentJobs, dialog.HorizontalFirst);
        ApplyRenumbering(finalSorted, dialog.Prefix, dialog.Suffix, dialog.StartNumber, dialog.Digits);
        foreach (var job in currentJobs)
        {
            // 图号重排后，打印顺序应重新按新图号计算，清掉右键“移到第一个”的手动优先级。
            job.SortPriority = 0;
        }
        _grid.Items.Refresh();
        // 图号重排窗口中的方向同时作为下次默认值，并与其它位置排序入口保持一致。
        _settings.SortOrderHorizontalFirst = dialog.HorizontalFirst;
        AppSettingsStore.Save(_settings);
        SortAndRefreshOutputPaths();

        // 反写 CAD 文件中的图号（批量：共享一次文档锁定/事务/图框库加载，逐张写在图多时会明显变慢）
        var updated = CadTextUpdater.UpdateDrawingNumbers(finalSorted, _currentDocument,
            failure => AppendLog("WARN", failure));

        AppendLog("INFO", $"图号重排完成，{finalSorted.Count} 张图框按布局顺序、" + (_settings.SortOrderHorizontalFirst ? "从左到右、从上到下" : "从上到下、从左到右") + $"排序，已反写 CAD {updated} 处。");
    }

    private static void ApplyRenumbering(IReadOnlyList<PlotJob> sorted, string prefix, string suffix, int start, int digits = 0)
    {
        if (digits <= 0)
        {
            var maxNumber = sorted.Count + start - 1;
            digits = Math.Max(2, maxNumber.ToString().Length);
        }

        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].DrawingNumber = prefix + (start + i).ToString($"D{digits}") + suffix;
            sorted[i].CadDrawingNumber = sorted[i].DrawingNumber;
        }
    }

    /// <summary>从现有图号中检测公共前缀：取最长公共前缀后去掉末尾数字。</summary>
    private static string DetectCommonDrawingNumberPrefix(IReadOnlyList<PlotJob> jobs)
    {
        if (jobs.Count == 0) return "";
        var numbers = jobs.Select(j => j.DrawingNumber).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (numbers.Count == 0) return "";

        // 最长公共前缀
        var common = numbers[0];
        for (var i = 1; i < numbers.Count && common.Length > 0; i++)
        {
            var len = Math.Min(common.Length, numbers[i].Length);
            var j = 0;
            while (j < len && common[j] == numbers[i][j]) j++;
            common = common.Substring(0, j);
        }

        // 去掉末尾数字部分（如 JZ-0 → JZ-、JG0 → JG）
        while (common.Length > 0 && char.IsDigit(common[common.Length - 1]))
            common = common.Substring(0, common.Length - 1);

        return common;
    }

    /// <summary>
    /// 图号重排只处理当前 DWG：先按 CAD 布局 TabOrder（模型空间在前）分组，
    /// 再在每个布局内部按窗口选择的空间方向排序，禁止跨布局直接比较坐标。
    /// </summary>
    private List<PlotJob> SortRenumberJobsByLayout(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        var tabOrders = ReadCurrentLayoutTabOrders();
        var result = new List<PlotJob>(jobs.Count);
        var layoutGroups = jobs
            .GroupBy(job => job.SpaceName ?? "", StringComparer.Ordinal)
            .OrderBy(group => tabOrders.TryGetValue(group.Key, out var tabOrder) ? tabOrder : int.MaxValue)
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var layoutGroup in layoutGroups)
        {
            result.AddRange(SpatialSorter.Sort(layoutGroup.ToList(), horizontalFirst));
        }

        return result;
    }

    private Dictionary<string, int> ReadCurrentLayoutTabOrders()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var tr = _currentDocument.Database.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(_currentDocument.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId recordId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(recordId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                var layout = (Layout)tr.GetObject(owner.LayoutId, OpenMode.ForRead);
                result[layout.LayoutName] = layout.TabOrder;
            }

            tr.Commit();
        }
        catch (Exception ex)
        {
            // 极少数宿主在非命令上下文读取布局失败时，仍按布局名稳定分组，绝不退回跨布局混排。
            AppendLog("WARN", "读取布局顺序失败，图号重排将按布局名排序: " + ex.Message);
        }

        return result;
    }

    /// <summary>按当前图框块排序设置生成最终清单顺序。</summary>
    private List<PlotJob> SortTitleBlockJobs(IReadOnlyList<PlotJob> jobs)
    {
        if (_settings.TitleBlockBatchSortMode == TitleBlockSortMode.Spatial)
        {
            // 位置模式与矩形批打完全一致：不夹带图号、图名或“移到第一个”的手工优先级。
            return SortSpatialGroups(jobs);
        }

        var sorted = jobs
            .OrderByDescending(x => x.SortPriority)
            .ThenBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // 图号和图名都相同时，用位置顺序作最后的业务排序依据。
        return SpatiallyBreakTies(sorted);
    }

    /// <summary>
    /// 对已按（图号, 图名）排序的列表，在图号和图名完全相同的连续组内按空间位置（摆放顺序）二次排序。
    /// </summary>
    private List<PlotJob> SpatiallyBreakTies(List<PlotJob> sortedJobs)
    {
        if (sortedJobs.Count <= 1) return sortedJobs;
        var result = new List<PlotJob>(sortedJobs.Count);
        var i = 0;
        while (i < sortedJobs.Count)
        {
            var anchor = sortedJobs[i];
            var j = i + 1;
            // 收集图号、图名、优先级全部相同的连续段
            while (j < sortedJobs.Count
                && sortedJobs[j].SortPriority == anchor.SortPriority
                && NaturalStringComparer.Instance.Compare(sortedJobs[j].DrawingNumber, anchor.DrawingNumber) == 0
                && string.Equals(sortedJobs[j].Title, anchor.Title, StringComparison.CurrentCultureIgnoreCase))
            {
                j++;
            }
            var group = sortedJobs.GetRange(i, j - i);
            if (group.Count > 1)
            {
                // 不同 DWG/布局的坐标系互不相关，只允许在同一源图、同一空间内比较位置。
                group = SortSpatialGroups(group);
            }
            result.AddRange(group);
            i = j;
        }
        return result;
    }

    /// <summary>
    /// 按“源 DWG → 布局 → 图中位置”排序。源文件沿用加入清单的顺序，布局严格按 CAD TabOrder，
    /// 从而避免把两个不同图形中相同坐标的图框错误地交叉排列。布局内部与矩形批打直接
    /// 共用 SpatialSorter.SortByLayout；位置模式不得再用“移到第一个”优先级切割空间分组。
    /// </summary>
    private List<PlotJob> SortSpatialGroups(IReadOnlyList<PlotJob> jobs)
    {
        var result = new List<PlotJob>(jobs.Count);
        var sourceGroups = jobs
            .GroupBy(job => GetSourceGroupKey(job.SourceFile), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => GetSourceGroupOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var sourceGroup in sourceGroups)
        {
            result.AddRange(SpatialSorter.SortByLayout(
                sourceGroup.ToList(),
                _settings.SortOrderHorizontalFirst));
        }

        return result;
    }

    private int GetSourceGroupOrder(string sourceFile)
    {
        for (var index = 0; index < _selectedDwgFiles.Count; index++)
        {
            if (AreSamePath(_selectedDwgFiles[index], sourceFile))
            {
                return index;
            }
        }

        // 当前图直接扫描时 _selectedDwgFiles 为空，应排在其它无法识别来源的任务之前。
        if (AreSamePath(_currentDocument.Database.Filename, sourceFile)
            || string.Equals(_currentDocument.Name, sourceFile, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        return int.MaxValue;
    }

    private static string GetSourceGroupKey(string? sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return "";
        }

        try
        {
            return Path.GetFullPath(sourceFile);
        }
        catch
        {
            return sourceFile?.Trim() ?? "";
        }
    }

    private static bool AreSamePath(string? left, string? right)
    {
        return string.Equals(
            GetSourceGroupKey(left),
            GetSourceGroupKey(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private void ClearJobs()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_jobs.Count == 0)
        {
            return;
        }

        if (System.Windows.MessageBox.Show("确定清空当前图纸清单吗？", "批量打印", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        _jobs.Clear();
        _selectedDwgFiles.Clear();
        RebuildDuplicateIdentitySets();
        ClearSequenceOverlay();
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }
        RefreshStatus();
    }

    private string BuildOutputPath(
        PlotJob job,
        int sequenceNumber,
        int sequenceDigits,
        ISet<string> reservedPaths)
    {
        var baseName = FileNameSanitizer.FormatFileNamePattern(
            _settings.PdfFileNamePattern,
            job,
            sequenceNumber,
            sequenceDigits,
            _settings.LongPaperNameFormat,
            _settings.LongPaperSnapToleranceMm);
        return FileNameSanitizer.MakeUnique(
            GetOutputDirectory(job),
            baseName,
            reservedPaths,
            _settings.AddSequenceWhenPdfExists,
            SelectedOutputExtension,
            createDirectory: false);
    }

    private string GetDefaultOutputDirectory() => GetSelectedCadDirectory();

    private void ChooseOutputDirectory()
    {
        // WPF 没有等价的目录选择对话框，保留 WinForms FolderBrowserDialog。
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择输出目录",
            SelectedPath = _outputDirectory.Text
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputDirectory.Text = dialog.SelectedPath;
            _outputDirectoryIsCustom = true;
            SaveCurrentSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "输出目录切换为 " + dialog.SelectedPath);
        }
    }

    private string GetSelectedCadDirectory()
    {
        var selectedFile = _selectedDwgFiles.FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(selectedFile))
        {
            return Path.GetDirectoryName(selectedFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var firstJobFile = _jobs
            .Select(x => x.SourceFile)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && File.Exists(x));
        if (!string.IsNullOrWhiteSpace(firstJobFile))
        {
            return Path.GetDirectoryName(firstJobFile) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        var file = _currentDocument.Database.Filename;
        return string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void ApplyManuallyEnteredOutputDirectory()
    {
        if (!_outputDirectoryModified)
        {
            return;
        }

        _outputDirectoryModified = false;
        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            _outputDirectoryIsCustom = false;
            UpdateAutomaticOutputDirectory();
        }
        else
        {
            _outputDirectoryIsCustom = true;
            _outputDirectory.Text = directory;
        }

        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "输出目录切换为 " + directory);
    }

    private void GenerateDrawingDirectory()
    {
        if (_jobs.Count == 0)
        {
            System.Windows.MessageBox.Show("当前没有可生成目录的图纸清单。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ok = false;
        var message = "";
        CadWindowFocus.HideForCadInput(this);
        try
        {
            ok = DirectoryTableGenerator.PromptAndGenerate(_currentDocument, _jobs.ToList(), _settings, out message);
            AppendLog(ok ? "INFO" : "WARN", message);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }

        // 主窗口恢复后再显示结果提示，避免无主提示框落到其他程序后面。
        System.Windows.MessageBox.Show(message, "批量打印", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SplitSelectedDwgs()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var selectedJobs = _jobs.Where(x => x.Selected).ToList();
        if (selectedJobs.Count == 0)
        {
            System.Windows.MessageBox.Show("请先勾选需要拆图的图纸。", "批量拆图", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"将按当前勾选清单拆出 {selectedJobs.Count} 个 DWG 文件。\n\n模型空间: 新建轻量 DWG，只复制图框范围内或相交对象，打开后自动居中显示。\n布局空间: 不动模型空间，只保留当前布局并清理布局内其他图素。\n\n输出位置: {GetOutputLocationDescription()}。\n\n是否继续？",
            "批量拆图",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        Cursor = Cursors.Wait;
        IsEnabled = false;
        try
        {
            SaveCurrentSettings();
            AppendLog("INFO", $"开始批量拆图，共 {selectedJobs.Count} 张。");
            var results = DwgSplitService.SplitMany(
                selectedJobs,
                _currentDocument,
                _settings,
                job => AppendLog("INFO", $"开始拆图 {job.DrawingNumber}_{job.Title}"),
                customOutputDirectory: CustomOutputDirectory,
                sourceSubfolder: AutomaticOutputSubfolder,
                explicitOutputPaths: selectedJobs.ToDictionary(job => job, job => job.OutputPath));

            var success = results.Count(x => x.Error == null);
            var failed = results.Count - success;
            foreach (var result in results)
            {
                if (result.Error == null)
                {
                    result.Job.OutputPath = result.OutputPath;
                    var actionText = result.Job.IsPaperSpace ? "清理" : "跳过";
                    AppendLog("INFO", $"拆图成功 {result.OutputPath}，保留 {result.KeptEntities} 个对象，{actionText} {result.RemovedEntities} 个对象，未知外包框保留 {result.UnknownExtentsKept} 个。");
                }
                else
                {
                    AppendLog("ERROR", $"拆图失败 {result.Job.DrawingNumber}_{result.Job.Title}: {result.Error.Message}");
                }
            }

            _grid.Items.Refresh();

            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            RefreshStatus();

            var logText = string.IsNullOrWhiteSpace(_lastLogPath)
                ? ""
                : $"\n日志:\n{_lastLogPath}";
            var failedText = failed == 0
                ? ""
                : "\n\n失败项:\n" + string.Join("\n", results.Where(x => x.Error != null).Take(20).Select(x => $"{x.Job.DrawingNumber}_{x.Job.Title}: {x.Error!.Message}"));
            System.Windows.MessageBox.Show(
                $"拆图完成: 成功 {success} 张，失败 {failed} 张。{logText}{failedText}",
                "批量拆图",
                MessageBoxButton.OK,
                failed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            IsEnabled = true;
            Cursor = Cursors.Arrow;
        }
    }

    private void ManageLibrary()
    {
        var form = new TitleBlockLibraryManagerForm();
        CadDialog.ShowModal(form);
        if (form.LibraryChanged)
        {
            ScanCurrentDrawing();
        }
    }

    private void ShowSettings()
    {
        ShowSettingsAtTab(SettingsForm.InitialTabIndex);
    }

    private void ShowSettingsAtTab(int tabIndex)
    {
        while (true)
        {
            SettingsForm.InitialTabIndex = tabIndex;
            // 批打窗口可能在用户切换/新建图纸后仍保持打开；设置页始终绑定当前活动图纸。
            var form = new SettingsForm();
            if (CadDialog.ShowModal(form) != true)
            {
                return;
            }

            ReloadSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "设置已更新。");

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                tabIndex = form.SelectedTabIndex;
                return;
            }

            tabIndex = form.SelectedTabIndex;
            PickDirectorySettingFromCad(
                form.RequestPickDirectoryRowHeight,
                form.RequestPickDirectoryTextAppearance,
                form.RequestedDirectoryColumnKey);
        }
    }

    private void ShowSortSettings()
    {
        var dialog = new SortOrderDialog(
            _settings.SortOrderHorizontalFirst,
            showSortBasis: true,
            sortMode: _settings.TitleBlockBatchSortMode);
        if (CadDialog.ShowModal(dialog) != true) return;

        _settings.TitleBlockBatchSortMode = dialog.SortMode;
        _settings.SortOrderHorizontalFirst = dialog.HorizontalFirst;
        AppSettingsStore.Save(_settings);

        // 最终排序只能经过统一入口，否则位置预排会被随后的图号排序覆盖。
        SortAndRefreshOutputPaths();
        var modeName = _settings.TitleBlockBatchSortMode == TitleBlockSortMode.Spatial
            ? "按图纸位置"
            : "按图号（同号同名按位置）";
        var orderName = _settings.SortOrderHorizontalFirst ? "从左到右，从上到下" : "从上到下，从左到右";
        AppendLog("INFO", $"已按\"{modeName}；{orderName}\"重排图框顺序。");
    }

    private void PickDirectorySettingFromCad(
        bool pickRowHeight,
        bool pickTextAppearance,
        string? columnKey)
    {
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var document = GetActiveCadDocument();
            if (document == null)
            {
                const string noDocumentMessage = "当前没有可用的 CAD 图纸，请先打开图纸后重试。";
                AppendLog("WARN", noDocumentMessage);
                System.Windows.MessageBox.Show(noDocumentMessage, "批量打印设置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var settings = AppSettingsStore.Load();
            bool ok;
            string message;
            if (pickTextAppearance)
            {
                ok = DirectoryTableGenerator.PromptTextAppearance(document, settings, out _, out message);
            }
            else if (pickRowHeight)
            {
                ok = DirectoryTableGenerator.PromptRowHeight(document, settings, out _, out message);
            }
            else
            {
                ok = DirectoryTableGenerator.PromptColumnSize(
                    document,
                    settings,
                    columnKey ?? "",
                    out _,
                    out message);
            }
            ReloadSettings();
            AppendLog(ok ? "INFO" : "WARN", message);
            System.Windows.MessageBox.Show(message, "批量打印设置", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private static Document? GetActiveCadDocument()
    {
        try
        {
            return CadApp.DocumentManager.MdiActiveDocument;
        }
        catch
        {
            return null;
        }
    }

    private void ReloadSettings()
    {
        var updated = AppSettingsStore.Load();
        _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
        _settings.HideFrameBoundaryWhenPlotting = updated.HideFrameBoundaryWhenPlotting;
        _settings.PlotTransparency = updated.PlotTransparency;
        _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
        _settings.MergePdf = updated.MergePdf;
        _settings.UseFileNameAsPdfBookmark = updated.UseFileNameAsPdfBookmark;
        _settings.MergePdfByPaperSize = updated.MergePdfByPaperSize;
        _settings.OpenOutputDirectoryAfterBatchPrint = updated.OpenOutputDirectoryAfterBatchPrint;
        _settings.OpenMergedPdfAfterMerge = updated.OpenMergedPdfAfterMerge;
        _settings.GeneratePrintLog = updated.GeneratePrintLog;
        _settings.ConvertTextToGeometryWhenPlotting = updated.ConvertTextToGeometryWhenPlotting;
        _settings.PdfFileNamePattern = updated.PdfFileNamePattern;
        _settings.PdfFileNameSeparator = updated.PdfFileNameSeparator;
        _settings.PdfFileNameFields = updated.PdfFileNameFields.ToList();
        _settings.FileNameSequenceDigits = updated.FileNameSequenceDigits;
        _settings.AutoFileNameSequenceDigits = updated.AutoFileNameSequenceDigits;
        _settings.FileNameSequenceStartNumber = updated.FileNameSequenceStartNumber;
        _settings.OpenExternalDwgForPlot = updated.OpenExternalDwgForPlot;
        _settings.DirectoryIndexWidth = updated.DirectoryIndexWidth;
        _settings.DirectoryNumberWidth = updated.DirectoryNumberWidth;
        _settings.DirectoryTitleWidth = updated.DirectoryTitleWidth;
        _settings.DirectoryPaperWidth = updated.DirectoryPaperWidth;
        _settings.DirectoryRemarkWidth = updated.DirectoryRemarkWidth;
        _settings.DirectoryRowHeight = updated.DirectoryRowHeight;
        _settings.DirectoryTextHeightRatio = updated.DirectoryTextHeightRatio;
        _settings.DirectoryTextStyleName = updated.DirectoryTextStyleName;
        _settings.DirectoryColorIndex = updated.DirectoryColorIndex;
        _settings.DirectoryTextHeight = updated.DirectoryTextHeight;
        _settings.DirectoryTextWidthFactor = updated.DirectoryTextWidthFactor;
        _settings.DirectoryLayerName = updated.DirectoryLayerName;
        _settings.DirectoryDrawHeader = updated.DirectoryDrawHeader;
        _settings.DirectoryDrawGridLines = updated.DirectoryDrawGridLines;
        _settings.DirectoryColumns = updated.DirectoryColumns.Select(x => x.Clone()).ToList();
        _settings.LongPaperNameFormat = updated.LongPaperNameFormat;
        _settings.LongPaperSnapToleranceMm = updated.LongPaperSnapToleranceMm;
        _settings.TitleBlockBatchSortMode = updated.TitleBlockBatchSortMode;
        _settings.SortOrderHorizontalFirst = updated.SortOrderHorizontalFirst;
        _settings.LastStyleSheet = updated.LastStyleSheet;
        _mergePdfCheckBox.IsChecked = updated.MergePdf;
    }

    private void ImportLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            Title = "导入图框库"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load(dialog.FileName);
        TitleBlockLibraryStore.Save(library);
        System.Windows.MessageBox.Show("图框库已导入。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportLibrary()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            FileName = "TitleBlockLibrary.json",
            Title = "导出图框库"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        TitleBlockLibraryStore.Save(TitleBlockLibraryStore.Load(), dialog.FileName);
        System.Windows.MessageBox.Show("图框库已导出。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PrintSelectedJobs()
    {
        var selected = _jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            System.Windows.MessageBox.Show("没有勾选任何图纸。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            System.Windows.MessageBox.Show($"未找到可用的 {SelectedOutputFormat} 输出设备。", "批量打印", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        // 保存策略已经决定每张图的最终目录；合并 PDF 直接放到同一目录，
        // 并使用源 CAD 文件名，不再重复询问用户保存位置。
        _mergedOutputPath = IsPdfOutput && _mergePdfCheckBox.IsChecked == true
            ? GetAutomaticMergedOutputPath(selected)
            : "";
        foreach (var directory in selected
                     .Select(job => Path.GetDirectoryName(job.OutputPath))
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(directory!);
        }
        ApplyLeaveMarginSelection(selected);
        HasPendingPrint = true;
        // 非模态窗口不能设置 DialogResult（WPF 会抛异常），由调用方在 Closed 后检查 HasPendingPrint。
        Close();
    }

    private void PrintOrStop()
    {
        if (_printCts != null)
        {
            _printCts.Cancel();
            return;
        }

        if (IsDwgOutput)
        {
            SplitSelectedDwgs();
            return;
        }

        PrintSelectedJobs();
    }

    private bool IsDwgOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "DWG",
        StringComparison.OrdinalIgnoreCase);

    private bool IsPdfOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "PDF",
        StringComparison.OrdinalIgnoreCase);

    private bool IsJpgOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "JPG",
        StringComparison.OrdinalIgnoreCase);

    private bool IsPngOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "PNG",
        StringComparison.OrdinalIgnoreCase);

    private bool SupportsLeaveMargin => !IsDwgOutput && !IsPngOutput && !IsJpgOutput;

    private bool IsDwfOutput => string.Equals(
        _outputFormatCombo.SelectedItem?.ToString(),
        "DWF",
        StringComparison.OrdinalIgnoreCase);

    private string SelectedOutputFormat => _outputFormatCombo.SelectedItem?.ToString()?.Trim() ?? "";

    private string SelectedOutputExtension => "." + SelectedOutputFormat.ToLowerInvariant();

    private string SelectedPlotDevice => IsPdfOutput
        ? AcadPlotterInstaller.PreferredPdfPlotter
        : IsJpgOutput ? _jpgPlotDevice
        : IsDwfOutput ? _dwfPlotDevice
        : _pngPlotDevice;

    private string? AutomaticOutputSubfolder => _savePathModeCombo.SelectedIndex == 1
                                                   && !string.IsNullOrWhiteSpace(SelectedOutputFormat)
        ? FileNameSanitizer.Clean(SelectedOutputFormat)
        : null;

    private string? CustomOutputDirectory => _outputDirectoryIsCustom
        ? _outputDirectory.Text.Trim()
        : null;

    private void RefreshSavePathModeOptions(bool preserveSelection)
    {
        var selectedIndex = preserveSelection && _savePathModeCombo.SelectedIndex >= 0
            ? Math.Min(_savePathModeCombo.SelectedIndex, 1)
            : 0;
        var format = SelectedOutputFormat;
        var formatPathText = string.IsNullOrWhiteSpace(format)
            ? "源文件路径/输出格式"
            : "源文件路径/" + format;

        _savePathModeCombo.Items.Clear();
        _savePathModeCombo.Items.Add("源文件路径");
        _savePathModeCombo.Items.Add(formatPathText);
        _savePathModeCombo.SelectedIndex = selectedIndex;
    }

    private void ApplySelectedSavePathMode()
    {
        _outputDirectoryIsCustom = false;
        UpdateAutomaticOutputDirectory();
        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "保存路径切换为 " + _savePathModeCombo.SelectedItem);
    }

    private void UpdateAutomaticOutputDirectory()
    {
        _outputDirectory.Text = AppendAutomaticSubfolder(GetSelectedCadDirectory());
    }

    private string AppendAutomaticSubfolder(string sourceDirectory)
    {
        var subfolder = AutomaticOutputSubfolder;
        return string.IsNullOrWhiteSpace(subfolder)
            ? sourceDirectory
            : Path.Combine(sourceDirectory, subfolder);
    }

    private string GetOutputDirectory(PlotJob job)
    {
        if (_outputDirectoryIsCustom)
        {
            return _outputDirectory.Text.Trim();
        }

        var sourceFile = !string.IsNullOrWhiteSpace(job.SourceFile) && File.Exists(job.SourceFile)
            ? job.SourceFile
            : _currentDocument.Database.Filename;
        var sourceDirectory = string.IsNullOrWhiteSpace(sourceFile)
            ? GetSelectedCadDirectory()
            : Path.GetDirectoryName(sourceFile) ?? GetSelectedCadDirectory();
        return AppendAutomaticSubfolder(sourceDirectory);
    }

    private string GetOutputLocationDescription()
    {
        if (_outputDirectoryIsCustom)
        {
            return _outputDirectory.Text.Trim();
        }

        var subfolder = AutomaticOutputSubfolder;
        return string.IsNullOrWhiteSpace(subfolder)
            ? "每个源 DWG 所在目录"
            : $"每个源 DWG 所在目录下的 {subfolder} 文件夹";
    }

    private void UpdateOutputFormatUi()
    {
        RefreshSavePathModeOptions(preserveSelection: true);
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var plotOutput = !IsDwgOutput;
        foreach (var control in _plotOnlyControls)
        {
            control.IsEnabled = plotOutput;
        }
        _styleSettingsButton.IsEnabled = plotOutput && _styleCombo.SelectedIndex >= 0;
        _mergePdfCheckBox.IsEnabled = IsPdfOutput;
        // PNG/JPG 使用像素介质，不支持毫米纸张扩展或按毫米缩放留白；保留勾选状态供切回 PDF/DWF。
        _leaveMarginCheckBox.IsEnabled = SupportsLeaveMargin;
        _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;

        _outputNameColumn.Header = SelectedOutputFormat + "文件名";

        SortAndRefreshOutputPaths();
    }

    public void ExecutePendingPrint()
    {
        if (!HasPendingPrint)
        {
            return;
        }

        var selected = _jobs.Where(x => x.Selected).ToList();
        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        var mergePdf = !string.IsNullOrWhiteSpace(_mergedOutputPath);
        var originalOutputPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedSuccessfully = false;
        var mergedOutputPaths = new List<string>();
        var completed = 0;

        ShowSequenceOverlayForPrint(selected);
        // 切换按钮为"停止"状态
        _printCts = new CancellationTokenSource();
        _printButton.Content = "停止";
        _printButton.Background = new SolidColorBrush(Color.FromRgb(200, 40, 40));
        _printButton.BorderBrush = new SolidColorBrush(Color.FromRgb(160, 30, 30));
        _printButton.IsEnabled = true;

        void PumpMessages()
        {
            // 原 Application.DoEvents：处理完挂起的 UI 消息后再继续。
            Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        }

        try
        {
            var failed = new List<string>();
            PrepareCustomPaperRegistrations(selected, device);
            if (mergePdf)
            {
                temporaryDirectory = CreateTemporaryPdfDirectory("Merge");
                for (var i = 0; i < selected.Count; i++)
                {
                    selected[i].OutputPath = Path.Combine(
                        temporaryDirectory,
                        (i + 1).ToString("D5") + ".pdf");
                }
            }

            _statusLabel.Text = $"打印中... 0 / {selected.Count}";
            PumpMessages();

            var results = PlotterService.PlotMany(
                selected,
                device,
                style,
                _currentDocument,
                _settings,
                job =>
                {
                    completed++;
                    _statusLabel.Text = $"打印中... {completed} / {selected.Count}";
                    AppendLog(
                        "INFO",
                        $"开始打印 {job.DrawingNumber}_{job.Title}；源文件={job.SourceFile}；布局={job.SpaceName}；输出={job.OutputPath}");
                    PumpMessages();
                },
                _printCts.Token);

            foreach (var result in results)
            {
                var job = result.Job;
                if (result.Succeeded)
                {
                    AppendLog("INFO", $"打印成功 {job.OutputPath}");
                    continue;
                }

                var ex = result.Error!;
                var message = $"{job.DrawingNumber}_{job.Title}: {ex.Message}";
                failed.Add(message);
                AppendLog("ERROR", ex.ToString());
                AppendLog("ERROR", "打印失败，" + message);
            }

            foreach (var skipped in selected.Except(results.Select(x => x.Job)))
            {
                var message = $"{skipped.DrawingNumber}_{skipped.Title}: 文件打开失败，未开始打印。";
                failed.Add(message);
                AppendLog("ERROR", message);
            }

            var printed = results.Count(x => x.Succeeded);
            if (mergePdf && failed.Count == 0 && printed == selected.Count)
            {
                try
                {
                    _statusLabel.Text = "正在合并 PDF...";
                    PumpMessages();
                    var mergeInputs = selected.Select(job => new PdfMergeInput(
                        job.OutputPath,
                        Path.GetFileNameWithoutExtension(originalOutputPaths[job]),
                        OutputPaperNameResolver.Resolve(
                            job,
                            _settings.LongPaperSnapToleranceMm),
                        job.PaperWidthMm,
                        job.PaperHeightMm)).ToList();
                    var mergePlans = PdfDocumentService.PlanMerges(
                        mergeInputs,
                        _mergedOutputPath,
                        _settings.MergePdfByPaperSize,
                        _settings.AddSequenceWhenPdfExists);
                    foreach (var mergePlan in mergePlans)
                    {
                        PdfDocumentService.Merge(
                            mergePlan.Inputs,
                            mergePlan.OutputPath,
                            _settings.UseFileNameAsPdfBookmark);
                        mergedOutputPaths.Add(mergePlan.OutputPath);
                        AppendLog("INFO", $"合并 PDF 成功 {mergePlan.OutputPath}");
                    }
                    mergedSuccessfully = true;
                }
                catch (Exception ex)
                {
                    var message = "合并 PDF 失败: " + ex.Message;
                    failed.Add(message);
                    AppendLog("ERROR", ex.ToString());
                }
            }

            var printLogPath = SavePrintLogIfEnabled();
            var printLogText = string.IsNullOrWhiteSpace(printLogPath) ? "" : $"\n日志: {printLogPath}";
            _statusLabel.Text = $"完成，共 {printed} 张";
            var mergedFilesText = string.Join("\n", mergedOutputPaths);
            var summary = mergePdf
                ? mergedSuccessfully
                    ? $"打印并合并完成: 共 {printed} 张，生成 {mergedOutputPaths.Count} 个 PDF。\n合并文件:\n{mergedFilesText}{printLogText}"
                    : $"打印完成，但 PDF 合并未全部完成。\n成功打印 {printed} 张，失败 {failed.Count} 项，已生成 {mergedOutputPaths.Count} 个合并 PDF。{printLogText}"
                : $"打印完成: 成功 {printed} 张，失败 {failed.Count} 张。{printLogText}";
            if (failed.Count > 0)
            {
                summary += "\n\n失败项:\n" + string.Join("\n", failed);
            }

            System.Windows.MessageBox.Show(summary, "批量打印", MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (!mergePdf && printed > 0 && _settings.OpenOutputDirectoryAfterBatchPrint)
            {
                OpenOutputDirectoryAfterPrint();
            }
            else if (mergePdf && mergedSuccessfully && _settings.OpenMergedPdfAfterMerge)
            {
                OpenMergedPdfFiles(mergedOutputPaths);
            }
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = $"已停止（已完成 {completed} / {selected.Count}）";
            AppendLog("INFO", $"用户取消打印，已完成 {completed} / {selected.Count}");
            var printLogPath = SavePrintLogIfEnabled();
            var printLogText = string.IsNullOrWhiteSpace(printLogPath) ? "" : $"\n日志: {printLogPath}";
            System.Windows.MessageBox.Show($"打印已停止。\n已完成 {completed} / {selected.Count} 张。{printLogText}", "批量打印", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "打印失败";
            AppendLog("ERROR", ex.ToString());
            var printLogPath = SavePrintLogIfEnabled();
            var printLogText = string.IsNullOrWhiteSpace(printLogPath) ? "" : $"\n日志: {printLogPath}";
            System.Windows.MessageBox.Show("打印失败: " + ex.Message + printLogText, "批量打印", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            foreach (var pair in originalOutputPaths)
            {
                pair.Key.OutputPath = pair.Value;
            }

            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                TryDeleteDirectory(temporaryDirectory);
            }

            ClearSequenceOverlay();

            // 恢复按钮
            _printCts?.Dispose();
            _printCts = null;
            _printButton.Content = "开始打印";
            _printButton.Background = new SolidColorBrush(Color.FromRgb(0, 120, 215));
            _printButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 95, 170));

            RefreshStatus();
        }
    }

    private void OpenOutputDirectoryAfterPrint(string? outputFile = null)
    {
        var directory = !string.IsNullOrWhiteSpace(outputFile)
            ? Path.GetDirectoryName(outputFile) ?? ""
            : _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(outputFile) && File.Exists(outputFile))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + Path.GetFullPath(outputFile) + "\"",
                    UseShellExecute = true
                });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + Path.GetFullPath(directory) + "\"",
                WorkingDirectory = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog("WARN", "打开输出目录失败: " + ex.Message);
        }
    }

    private void OpenMergedPdfFiles(IEnumerable<string> outputFiles)
    {
        foreach (var outputFile in outputFiles.Where(File.Exists))
        {
            try
            {
                // 按纸张尺寸分组时可能生成多个合并文件；每个文件都交给系统默认 PDF 阅读器打开。
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(outputFile),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppendLog("WARN", "打开合并 PDF 失败: " + ex.Message);
            }
        }
    }

    private void ApplyLeaveMarginSelection(IEnumerable<PlotJob> jobs)
    {
        // 即使用户切换格式前曾勾选留白，PNG/JPG 作业也必须强制关闭，不能只依赖控件禁用状态。
        var leaveMargin = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;
        var marginMm = ReadMarginValue(_marginInput);
        foreach (var job in jobs)
        {
            // 留白选项是本次打印设置，不改变图框识别数据，只在输出/预览时生效。
            job.LeavePaperMargin = leaveMargin;
            job.PaperMarginMm = marginMm;
            // 正留白需要临时扩大纸张；负留白/关闭留白仍须保留扫描阶段识别出的任意纸张注册要求。
            job.RequiresCustomPaperRegistration =
                job.DetectedRequiresCustomPaperRegistration || (leaveMargin && marginMm > 0);
            if (!leaveMargin || marginMm <= 0)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
            }
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PlotJob job)
        {
            PreviewJob(job);
        }
    }

    private void PreviewJob(PlotJob job)
    {
        // 预览必须使用当前输出格式对应的绘图器，确保纸张、旋转和实际输出效果一致。
        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            System.Windows.MessageBox.Show("请选择打印机。", "打印预览", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        job.LeavePaperMargin = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;
        job.PaperMarginMm = ReadMarginValue(_marginInput);
        var wasVisible = IsVisible;
        var selectedRows = _grid.SelectedItems.OfType<PlotJob>().ToList();
        var currentCell = _grid.CurrentCell;
        try
        {
            _grid.UnselectAll();
            CadWindowFocus.HideForCadInput(this);
            // 预览任一图纸前也按当前勾选集合一次性准备全部纸张；当前行即使未勾选，也必须纳入本次准备。
            var previewJobs = _jobs
                .Where(candidate => candidate.Selected || ReferenceEquals(candidate, job))
                .ToList();
            // 预览时同步给所有准备作业应用当前留白设置，保证扩大/缩比例模式即时生效。
            ApplyLeaveMarginSelection(previewJobs);
            PrepareCustomPaperRegistrations(previewJobs, device);
            AppendLog("INFO", $"CAD 内部预览 {job.DrawingNumber}_{job.Title}");
            PlotterService.Preview(job, device, style, _currentDocument);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "打印预览失败: " + ex);
            System.Windows.MessageBox.Show("打印预览失败: " + ex.Message, "打印预览", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (wasVisible)
            {
                CadWindowFocus.RestoreDialog(this);
            }
            RestoreGridSelection(selectedRows, currentCell);
        }
    }

    /// <summary>
    /// 汇总本批任意纸张及正留白扩大纸张，并一次性写入当前 PDF/DWF 绘图器的 PMP。
    /// </summary>
    private void PrepareCustomPaperRegistrations(IReadOnlyList<PlotJob> jobs, string deviceName)
    {
        var result = CustomPaperBatchPreparer.Prepare(jobs, deviceName);
#if ZWCAD
        // 中望的图框库任意纸张仍须精确注册、精确选中实际介质，但不能强制使用
        // SetCustomPrintScale；该组合在标题栏批打中会生成页幅正确却没有内容的白页。
        // 这里只恢复标题栏批打原有的 ScaleToFit，矩形批打、单张打印和 AutoCAD 路径不受影响。
        foreach (var job in jobs.Where(job =>
                     string.Equals(job.PaperName, PaperSizeDetector.CustomPaperName, StringComparison.OrdinalIgnoreCase)))
        {
            job.UseExactWindowScale = false;
        }
#endif
        if (result.Registrations.Count == 0)
        {
            return;
        }

        var sizes = string.Join(", ", result.Registrations.Select(registration =>
            $"{registration.WidthMm:0.######}x{registration.HeightMm:0.######}mm({(registration.WasAdded ? "新增" : "复用")})"));
        AppendLog("INFO", $"PDF/DWF 自定义纸张已一次性准备，共 {result.Registrations.Count} 种: {sizes}");
#if AUTOCAD
        if (!string.IsNullOrWhiteSpace(result.AttachmentMessage))
        {
            AppendLog("INFO", "AutoCAD 自定义纸张关联刷新: " + result.AttachmentMessage);
        }
#endif
    }

    private void RestoreGridSelection(IReadOnlyList<PlotJob> selectedRows, DataGridCellInfo currentCell)
    {
        try
        {
            _grid.UnselectAll();
            foreach (var job in selectedRows)
            {
                if (_jobs.Contains(job))
                {
                    _grid.SelectedItems.Add(job);
                }
            }

            if (currentCell.Item != null
                && _jobs.Contains(currentCell.Item)
                && currentCell.Column != null
                && _grid.Columns.Contains(currentCell.Column))
            {
                _grid.CurrentCell = currentCell;
            }
        }
        catch
        {
            // 预览窗口退出后 CAD/WPF 可能重置选择状态，恢复失败不影响打印主流程。
        }
    }

    private string GetAutomaticMergedOutputPath(IReadOnlyList<PlotJob> selected)
    {
        var firstJob = selected.FirstOrDefault();
        var directory = firstJob == null ? "" : Path.GetDirectoryName(firstJob.OutputPath) ?? "";
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = firstJob == null ? _outputDirectory.Text.Trim() : GetOutputDirectory(firstJob);
        }

        var source = firstJob?.SourceFile;
        if (string.IsNullOrWhiteSpace(source))
        {
            source = _currentDocument.Database.Filename;
        }
        if (string.IsNullOrWhiteSpace(source))
        {
            source = _currentDocument.Name;
        }

        var baseName = FileNameSanitizer.Clean(Path.GetFileNameWithoutExtension(source));
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "合并图纸";
        }

        return Path.Combine(directory, baseName + ".pdf");
    }

    private static string CreateTemporaryPdfDirectory(string purpose)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "ZwcadBatchPlot",
            purpose,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDeleteDirectory(string? directory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch
        {
        }
    }

    private void ShowSequenceOverlayForPrint(IReadOnlyList<PlotJob> selected)
    {
        var currentJobs = selected.Where(IsCurrentDocumentJob).ToList();
        if (currentJobs.Count == 0)
        {
            ClearSequenceOverlay();
            return;
        }

        try
        {
            _sequenceOverlay.Show(currentJobs);
        }
        catch (Exception ex)
        {
            AppendLog("WARN", "打印临时序号标注显示失败: " + ex.Message);
        }
    }

    private void AppendLog(string level, string message)
    {
        if (!_settings.GeneratePrintLog)
        {
            return;
        }

        _logLines.Add(BatchPlotLogger.Format(level, message));
    }

    /// <summary>
    /// 打印日志由常规设置显式控制。关闭时既不创建目录/文件，也清空旧路径，
    /// 避免第二次打印的完成提示误显示上一次日志。
    /// </summary>
    private string SavePrintLogIfEnabled()
    {
        _lastLogPath = "";
        if (!_settings.GeneratePrintLog)
        {
            return "";
        }

        _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
        return _lastLogPath;
    }

    private void RefreshStatus()
    {
        var selected = _jobs.Count(x => x.Selected);
        var formatHint = IsDwgOutput
            ? "DWG 拆图"
            : IsPdfOutput ? "PDF 使用 LA_pdf" : $"{SelectedOutputFormat} 单张输出";
        var outputHint = $"{formatHint}，保存到{GetOutputLocationDescription()}。";
        _statusLabel.Text = $"共 {_jobs.Count} 张，已勾选 {selected} 张。{outputHint} 图框库: {TitleBlockLibraryStore.DefaultPath}";
    }

    private void SaveCurrentSettings()
    {
        _settings.LastPlotDevice = AcadPlotterInstaller.PreferredPdfPlotter;
        var style = PlotStyleManager.NormalizeStyleName(_styleCombo.SelectedItem?.ToString());
        if (!string.IsNullOrEmpty(style))
        {
            _settings.LastStyleSheet = style;
        }
        _settings.MergePdf = _mergePdfCheckBox.IsChecked == true;
        _settings.LeavePaperMargin = _leaveMarginCheckBox.IsChecked == true;
        _settings.PaperMarginMm = ReadMarginValue(_marginInput);
        AppSettingsStore.Save(_settings);
    }

    // ── UI 事件处理器 ──

    private void ScanCurrentDrawing_Click(object sender, RoutedEventArgs e) => ScanCurrentDrawing();

    private void ScanSelectedWindow_Click(object sender, RoutedEventArgs e) => ScanSelectedWindow();

    private void AddDwgFiles_Click(object sender, RoutedEventArgs e) => AddDwgFiles();

    private void ClearJobs_Click(object sender, RoutedEventArgs e) => ClearJobs();

    private void RenumberDrawingNumbers_Click(object sender, RoutedEventArgs e) => RenumberDrawingNumbers();

    private void GenerateDrawingDirectory_Click(object sender, RoutedEventArgs e) => GenerateDrawingDirectory();

    private void ChooseOutputDirectory_Click(object sender, RoutedEventArgs e) => ChooseOutputDirectory();

    private void PrintOrStop_Click(object sender, RoutedEventArgs e) => PrintOrStop();

    private void StyleSettings_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(this, _styleCombo.SelectedItem?.ToString());

    private void SortSettings_Click(object sender, RoutedEventArgs e) => ShowSortSettings();

    private void FileNameSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(1);

    private void DirectorySettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(2);

    private void GeneralSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(0);

    private void OutputDirectory_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_uiReady)
        {
            _outputDirectoryModified = true;
        }
    }

    private void OutputDirectory_LostFocus(object sender, RoutedEventArgs e) => ApplyManuallyEnteredOutputDirectory();

    private void OutputFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateOutputFormatUi();
    }

    private void SavePathMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        ApplySelectedSavePathMode();
    }

    private void Style_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _styleSettingsButton.IsEnabled = _styleCombo.SelectedIndex >= 0 && !IsDwgOutput;
        if (_styleSelectionReady)
        {
            SaveCurrentSettings();
        }
    }

    private void LeaveMargin_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMarginCheckBox.IsChecked == true;
        // 留白开关切换时立即更新所有作业状态，清除扩大纸张模式留下的精确纸张标记。
        ApplyLeaveMarginSelection(_jobs);
    }

    private void MarginInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        // 留白值改变时立即重置所有作业的扩大纸张状态，避免正↔负切换后遗留无效精确纸张标记。
        ApplyLeaveMarginSelection(_jobs);
    }

    /// <summary>留白下拉列表选项，+ 为扩大纸张，- 为缩比例。</summary>
    public sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    /// <summary>初始化留白下拉列表，正值=扩大纸张，负值=缩比例，整数1~10配对显示。</summary>
    public static void InitMarginCombo(ComboBox combo, int width, double savedValue)
    {
        combo.IsEditable = false;
        combo.Width = width;
        combo.Items.Clear();
        // 整数 1~10，每档先 + 再 -，共 20 项
        for (var n = 1; n <= 10; n++)
        {
            combo.Items.Add(new MarginOption { Value = n });
            combo.Items.Add(new MarginOption { Value = -n });
        }
        // 选中与保存值最接近的项
        var bestIdx = 0;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MarginOption opt)
            {
                var diff = Math.Abs(opt.Value - savedValue);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }
        }
        combo.SelectedIndex = bestIdx;
    }

    /// <summary>读取留白下拉列表的选中值（毫米）。</summary>
    public static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;
}

/// <summary>编号列：把 DataGridRow.AlternationIndex（0 起）转换为从 1 开始的行号文本。</summary>
public sealed class RowIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is int index && index >= 0 ? index + 1 : 0).ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 图号/图名重复项标红（原 CellFormatting 逻辑）：集合由 BatchPlotForm 在重排后刷新。
/// </summary>
public sealed class DuplicateBrushConverter : IValueConverter
{
    public static HashSet<string>? DrawingNumbers;
    public static HashSet<string>? Titles;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var text = (value as string)?.Trim();
        if (string.IsNullOrEmpty(text) || parameter as string != "DrawingNumber" && parameter as string != "Title")
        {
            return Brushes.Black;
        }

        var duplicates = parameter as string == "DrawingNumber" ? DrawingNumbers : Titles;
        if (text is null || duplicates is null)
        {
            return Brushes.Black;
        }

        return duplicates.Contains(text) ? Brushes.Red : Brushes.Black;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
