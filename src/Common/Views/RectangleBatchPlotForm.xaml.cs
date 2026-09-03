using System;
using System.Threading;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
/// 矩形框批量打印面板（WPF 版，非模态窗口；由 BatchPlotCommands 通过 CadDialog.ShowModeless 显示）。
/// </summary>
public sealed partial class RectangleBatchPlotForm : Window
{
    private sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    private sealed class Row : INotifyPropertyChanged
    {
        private string _fileName = "";
        private string _paperChoice = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> Options { get; set; } = new PaperDetection[0];

        /// <summary>纸张下拉候选（原 DataGridViewComboBoxCell.DataSource 语义）。</summary>
        public IReadOnlyList<string> PaperOptions =>
            Options.Select(PaperSizeDetector.FormatOption).ToList();

        public bool Selected
        {
            get => Job.Selected;
            set
            {
                if (Job.Selected == value) return;
                Job.Selected = value;
                OnPropertyChanged(nameof(Selected));
            }
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                if (string.Equals(_fileName, value, StringComparison.Ordinal)) return;
                _fileName = value;
                OnPropertyChanged(nameof(FileName));
            }
        }

        public string PaperChoice
        {
            get => _paperChoice;
            set
            {
                if (string.Equals(_paperChoice, value, StringComparison.Ordinal)) return;
                _paperChoice = value;
                OnPropertyChanged(nameof(PaperChoice));
            }
        }

        public string Scale { get; private set; } = "";

        /// <summary>编号列显示文本（1 基，随视图顺序刷新）。</summary>
        public string Number { get; private set; } = "";

        public void RefreshFromJob()
        {
            Scale = Job.ScaleText;
            OnPropertyChanged(nameof(Scale));
        }

        public void SetNumber(int index)
        {
            var text = index.ToString();
            if (string.Equals(Number, text, StringComparison.Ordinal)) return;
            Number = text;
            OnPropertyChanged(nameof(Number));
        }

        private void OnPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private readonly Document _document;
    private readonly AppSettings _settings;
    private readonly TemporarySequenceOverlay _overlay;
    private int _highlightedJobIndex = -1;
    private int _overlayScheduleGeneration;
    private readonly BindingList<Row> _rows = new();
    private readonly BindingList<Row> _displayRows = new();
    private CancellationTokenSource? _printCts;
    private CadSelectionWindow? _scanWindow;
    private TitleBlockScanScope? _lastScanScope;
    private bool _updating;
    private bool _updatingPrintSelection;
    private bool _viewSortedByHeader;
    private bool _outputDirectoryIsCustom;
    private bool _outputDirectoryModified;
    private bool _suppressTextEvents;
    private bool _suppressComboEvents;
    private bool _suppressPaperEvents;
    private string _sortMemberPath = "";
    private List<Row>? _pendingPrintToggleRows;
    private string _pngPlotDevice = "";
    private string _jpgPlotDevice = "";
    private string _dwfPlotDevice = "";
    private bool _styleSelectionReady;
    private List<(PlotJob Job, string DrawingNumber)>? _lastOverlayRebuildKey;
    private bool _overlayPainted;

    public RectangleBatchPlotForm(Document document)
    {
        _document = document;
        _settings = AppSettingsStore.Load();
        _overlay = new TemporarySequenceOverlay(document);
        InitializeComponent();
        InitializeGrid();
        InitMarginCombo(_marginInput, _settings.PaperMarginMm);
        _marginInput.IsEnabled = _leaveMargin.IsChecked == true;
        _leaveMargin.IsChecked = _settings.LeavePaperMargin;
        _mergePdf.IsChecked = _settings.MergePdf;
        LoadPlotOptions();
    }

    private void InitializeGrid()
    {
        _grid.ItemsSource = _displayRows;
    }

    protected override void OnClosed(EventArgs e)
    {
        _overlayScheduleGeneration++;
        // 关闭窗口时只清理临时红框和序号，不再退订或接管 CAD 删除命令。
        _overlay.Dispose();
        base.OnClosed(e);
    }

    /// <summary>兼容原 WinForms 调用方 form.Dispose() 的清理入口。</summary>
    public void Dispose() => Close();

    // ── 事件处理（XAML 绑定） ──

    private void ScanCurrentDrawing_Click(object sender, RoutedEventArgs e) => ScanCurrentDrawing();

    private void ScanSelectedWindow_Click(object sender, RoutedEventArgs e) => ScanSelectedWindow();

    private void ReloadFrames_Click(object sender, RoutedEventArgs e) => ReloadFrames();

    private void BrowseOutputDirectory_Click(object sender, RoutedEventArgs e) => ChooseOutputDirectory();

    private void OutputDirectory_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextEvents)
        {
            return;
        }

        // 对应原 TextBox.Modified：用户手动输入时置位。
        _outputDirectoryModified = true;
        RefreshOutputPaths();
    }

    private void OutputDirectory_LostFocus(object sender, RoutedEventArgs e)
        => ApplyManuallyEnteredOutputDirectory();

    private void SavePathMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents)
        {
            return;
        }

        ApplySelectedSavePathMode();
    }

    private void OutputFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents)
        {
            return;
        }

        UpdateOutputFormatUi();
    }

    private void Style_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _styleSettingsButton.IsEnabled = _style.SelectedIndex >= 0 && !IsDwgOutput;
        if (_styleSelectionReady)
        {
            SaveCurrentPlotOptions();
        }
    }

    private void StyleSettings_Click(object sender, RoutedEventArgs e)
        => PlotStyleManager.EditSelectedStyle(this, _style.SelectedItem?.ToString());

    private void LeaveMargin_CheckedChanged(object sender, RoutedEventArgs e)
        => _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMargin.IsChecked == true;

    private void PrintOrStop_Click(object sender, RoutedEventArgs e) => PrintOrStop();

    private void SortSettings_Click(object sender, RoutedEventArgs e) => ShowSortSettings();

    private void GeneralSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(0);

    private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // 原列头点击排序（Programmatic SortMode）。
        e.Handled = true;
        var columnIndex = _grid.Columns.IndexOf(e.Column);
        if (columnIndex < 0 || columnIndex >= _grid.Columns.Count)
        {
            return;
        }

        var memberPath = e.Column.SortMemberPath ?? "";
        if (_viewSortedByHeader && _sortMemberPath == memberPath)
        {
            _viewSortedByHeader = false;
            _sortMemberPath = "";
            RefreshDisplayRows();
            UpdateVisuals();
            return;
        }

        _viewSortedByHeader = true;
        _sortMemberPath = memberPath;
        var sorted = _rows.OrderBy(row => GetHeaderSortValue(row, memberPath), NaturalStringComparer.Instance).ToList();
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            ReplaceBindingListContents(_displayRows, sorted);
        }
        finally
        {
            _updating = wasUpdating;
        }
        UpdateDisplayIndexes();
        UpdateVisuals();
    }

    private string GetHeaderSortValue(Row row, string memberPath)
    {
        switch (memberPath)
        {
            case nameof(Row.Selected):
                return row.Selected ? "1" : "0";
            case nameof(Row.FileName):
                return row.FileName;
            case nameof(Row.PaperChoice):
                return row.PaperChoice;
            case nameof(Row.Scale):
                return row.Scale;
        }

        // 编号列按真实打印顺序排序；预览按钮列保持原始顺序。
        return _rows.IndexOf(row).ToString("D8");
    }

    private void Grid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = HitTestRow(e.OriginalSource as DependencyObject)?.Item as Row;
        if (row == null)
        {
            return;
        }

        if (e.OriginalSource is System.Windows.Controls.CheckBox
            && _grid.SelectedItems.Count > 1)
        {
            // 先记住点击前的多选行；点击复选框时可能会先改当前选择，后续统一同步这些行。
            var highlightedRows = HighlightedRows();
            _pendingPrintToggleRows = highlightedRows.Contains(row) ? highlightedRows : null;
        }

        // 原 CellClick：点击行时在 CAD 中高亮对应矩形框。
        _highlightedJobIndex = _rows.IndexOf(row);
        _overlay.SetHighlight(row.Job);
    }

    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = HitTestRow(e.OriginalSource as DependencyObject);
        if (container?.Item is not Row row)
        {
            return;
        }

        if (!_grid.SelectedItems.Contains(row))
        {
            if (_grid.SelectedItems.Count <= 1)
            {
                _grid.UnselectAll();
            }
            // 已经 Shift/Ctrl 多选后，即使鼠标移到其它行右键，也保留原多选集合用于批量操作。
            container.IsSelected = true;
        }

        if (_grid.SelectedItems.Contains(row))
        {
            _grid.CurrentItem = row;
        }
    }

    private static DataGridRow? HitTestRow(DependencyObject? source)
    {
        while (source != null && source is not DataGridRow)
        {
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return source as DataGridRow;
    }

    private void Grid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 只有多选行的候选纸张完全一致时，才允许统一修改纸张，避免套用到不适配的矩形框。
        _changePaperItem.IsEnabled = TryGetCommonPaperOptions(HighlightedRows(), out _);
    }

    private void PrintCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_updating || _updatingPrintSelection)
        {
            return;
        }

        if (((System.Windows.Controls.CheckBox)sender).DataContext is not Row row)
        {
            return;
        }

        // 对应原 GridCellValueChanged 的 Selected 分支。
        ApplyPrintSelectionToHighlightedRows(row);
        RemoveUnselectedRows();
        RefreshFileNames();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private void PaperChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _updatingPrintSelection || _suppressPaperEvents)
        {
            return;
        }

        if (((ComboBox)sender).DataContext is not Row row)
        {
            return;
        }

        // 对应原 GridCellValueChanged 的 PaperChoice 分支。
        var value = row.PaperChoice;
        var option = row.Options.FirstOrDefault(candidate => string.Equals(PaperSizeDetector.FormatOption(candidate), value, StringComparison.Ordinal));
        if (option != null)
        {
            ApplyPaper(row.Job, option);
            row.RefreshFromJob();
            RefreshFileNames();
            RefreshOutputPaths();
            UpdateVisuals();
        }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not Row row)
        {
            return;
        }

        if (IsDwgOutput)
        {
            MessageBox.Show("DWG 输出为拆图操作，不提供打印预览。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 预览与正式输出使用同一绘图器，避免不同格式之间出现纸张或旋转差异。
        var device = SelectedDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show($"未找到可用的 {SelectedOutputFormat} 输出设备。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var selectedRows = _grid.SelectedItems.OfType<Row>().ToList();
        var currentItem = _grid.CurrentItem as Row;
        CadWindowFocus.HideForCadInput(this);
        try
        {
            SaveCurrentPlotOptions();
            row.Job.LeavePaperMargin = SupportsLeaveMargin && _leaveMargin.IsChecked == true;
            row.Job.PaperMarginMm = ReadMarginValue(_marginInput);
            // 预览当前行时同时准备已勾选图纸的全部任意尺寸；当前行未勾选也不能漏掉。
            var previewJobs = _rows
                .Where(candidate => candidate.Selected || ReferenceEquals(candidate, row))
                .Select(candidate => candidate.Job)
                .ToList();
            // 同步留白设置到所有准备作业，保证扩大/缩比例模式即时生效。
            ApplyLeaveMarginSelection(previewJobs);
            CustomPaperBatchPreparer.Prepare(previewJobs, device);
            PlotterService.Preview(row.Job, device, SelectedStyle(), _document);
        }
        catch (Exception ex)
        {
            MessageBox.Show("打印预览失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
            RestoreGridSelection(selectedRows, currentItem);
        }
    }

    private void RestoreGridSelection(IReadOnlyList<Row> selectedRows, Row? currentItem)
    {
        try
        {
            _grid.UnselectAll();
            foreach (var row in selectedRows)
            {
                if (_displayRows.Contains(row))
                {
                    _grid.SelectedItems.Add(row);
                }
            }

            if (currentItem != null && _displayRows.Contains(currentItem))
            {
                _grid.CurrentItem = currentItem;
            }
        }
        catch
        {
            // CAD 预览退出后可能触发 DataGrid 选择状态变化；恢复失败不影响后续打印。
        }
    }

    private void BatchChangePaper_Click(object sender, RoutedEventArgs e) => BatchChangeHighlightedPaper();

    private void MarkNotPrint_Click(object sender, RoutedEventArgs e) => MarkHighlightedNotPrint();

    private void DeleteHighlighted_Click(object sender, RoutedEventArgs e) => DeleteHighlighted();

    // ── 数据加载 ──

    private void LoadRows(IReadOnlyList<RectangleFrameScanner.Result> results)
    {
        var rows = new List<Row>(results.Count);
        foreach (var result in results)
        {
            var option = result.PaperOptions[0];
            rows.Add(new Row
            {
                Job = result.Job,
                Options = result.PaperOptions,
                PaperChoice = PaperSizeDetector.FormatOption(option)
            });
        }

        ReplaceBindingListContents(_rows, rows);
        _viewSortedByHeader = false;
        _sortMemberPath = "";
        SortRows();
    }

    private void ReloadFrames()
    {
        try
        {
            List<RectangleFrameScanner.Result> results;
            if (_lastScanScope.HasValue)
            {
                results = RectangleFrameScanner.ScanScope(
                    _document,
                    _lastScanScope.Value,
                    _settings.PaperMatchToleranceMm,
                    _settings.RecognizeFourLineRectangleFrames);
            }
            else if (_scanWindow != null)
            {
                results = RectangleFrameScanner.ScanWindow(
                    _document,
                    _scanWindow,
                    _settings.PaperMatchToleranceMm,
                    _settings.RecognizeFourLineRectangleFrames);
            }
            else
            {
                return;
            }

            if (results.Count == 0)
            {
                // 设置关闭四线矩形识别后，旧列表中仅由四线组成的图框不能继续残留。
                LoadRows(results);
                MessageBox.Show("重新识别后没有找到矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            LoadRows(results);
        }
        catch (Exception ex)
        {
            MessageBox.Show("重新识别矩形框失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope();

    private void ScanCurrentDrawing()
    {
        var scope = PromptScanScope();
        if (scope == null)
        {
            return;
        }

        try
        {
            var results = RectangleFrameScanner.ScanScope(
                _document,
                scope.Value,
                _settings.PaperMatchToleranceMm,
                _settings.RecognizeFourLineRectangleFrames);
            if (results.Count == 0)
            {
                MessageBox.Show("扫描范围内没有识别到符合常见纸张比例的矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            _lastScanScope = scope;
            _scanWindow = null;
            LoadRows(results);
        }
        catch (Exception ex)
        {
            MessageBox.Show("扫描当前图失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ScanSelectedWindow()
    {
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var editor = _document.Editor;
            var first = editor.GetPoint(new PromptPointOptions("\n框选矩形图框扫描范围第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var second = editor.GetCorner(new PromptCornerOptions("\n框选矩形图框扫描范围对角点: ", first.Value));
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            // 保留原始 UCS 矩形；只把实体几何保留为 WCS，禁止在这里提前取 WCS 包围盒。
            var window = CadCoordinateSystem.CreateSelectionWindow(
                editor,
                first.Value,
                second.Value,
                _document.Database.TileMode);

            var results = RectangleFrameScanner.ScanWindow(
                _document,
                window,
                _settings.PaperMatchToleranceMm,
                _settings.RecognizeFourLineRectangleFrames);
            if (results.Count == 0)
            {
                MessageBox.Show("框选范围内没有识别到符合常见纸张比例的矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            _scanWindow = window;
            _lastScanScope = null;
            LoadRows(results);
        }
        catch (Exception ex)
        {
            MessageBox.Show("框选扫描失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private void TransformResultsToDcs(List<RectangleFrameScanner.Result> results)
    {
        try
        {
            var wcsToDcs = BatchPlotCommands.BuildWcsToDcsMatrix(_document.Editor);
            foreach (var result in results)
            {
                var job = result.Job;
                if (job.UsesUserCoordinateSystem)
                {
                    // UCS 模型任务必须等打印阶段对齐视图后再生成 DCS 窗口。
                    job.IsDcsWindow = false;
                    continue;
                }

                if (result.CornerPoints != null)
                {
                    // PlotJob 是打印与 DWG 拆图的共同载体。Min/Max 转为 DCS 前，必须保留 WCS 四角点。
                    job.CornerPoints = (double[])result.CornerPoints.Clone();
                    var corners = new[]
                    {
                        new Point3d(result.CornerPoints[0], result.CornerPoints[1], 0).TransformBy(wcsToDcs),
                        new Point3d(result.CornerPoints[2], result.CornerPoints[3], 0).TransformBy(wcsToDcs),
                        new Point3d(result.CornerPoints[4], result.CornerPoints[5], 0).TransformBy(wcsToDcs),
                        new Point3d(result.CornerPoints[6], result.CornerPoints[7], 0).TransformBy(wcsToDcs)
                    };
                    job.MinX = corners.Min(p => p.X);
                    job.MinY = corners.Min(p => p.Y);
                    job.MaxX = corners.Max(p => p.X);
                    job.MaxY = corners.Max(p => p.Y);
                }
                else
                {
                    BatchPlotCommands.TransformPlotWindow(job, wcsToDcs);
                }

                job.IsDcsWindow = true;
            }
        }
        catch (Exception ex)
        {
            _document.Editor.WriteMessage($"\n矩形框 WCS→DCS 变换失败，使用 WCS 坐标：{ex.Message}");
        }
    }

    private void SortRows()
    {
        if (_rows.Count == 0)
        {
            RefreshDisplayRows();
            UpdateVisuals();
            return;
        }

        _viewSortedByHeader = false;
        _sortMemberPath = "";
        var horizontalFirst = _settings.SortOrderHorizontalFirst;

        var allRows = _rows.ToList();
        var jobToRow = allRows.ToDictionary(row => row.Job, row => row);
        var sortedRows = SpatialSorter.SortByLayout(
                allRows.Select(row => row.Job).ToList(),
                horizontalFirst)
            .Select(job => jobToRow[job])
            .ToList();

        ReplaceBindingListContents(_rows, sortedRows);
        RefreshDisplayRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void RefreshDisplayRows()
    {
        var wasUpdating = _updating;
        _updating = true;
        try
        {
            ReplaceBindingListContents(_displayRows, _rows);
        }
        finally
        {
            _updating = wasUpdating;
        }

        UpdateDisplayIndexes();
    }

    /// <summary>
    /// 一次性替换绑定列表内容，只在结束时发送一次 Reset。
    /// 大图纸有数百个矩形框时，逐行 Add 会重复触发整表刷新，形成明显的 O(n²) 界面卡顿。
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

    private void UpdateDisplayIndexes()
    {
        for (var i = 0; i < _displayRows.Count; i++)
        {
            _displayRows[i].SetNumber(i + 1);
        }
    }

    private void RefreshFileNames()
    {
        var stem = Path.GetFileNameWithoutExtension(_document.Database.Filename);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = Path.GetFileNameWithoutExtension(_document.Name);
        }

        var digits = Math.Max(1, Math.Min(10, _settings.FileNameSequenceDigits));
        var printIndex = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Selected)
            {
                continue;
            }

            printIndex++;
            _rows[i].Job.DrawingNumber = printIndex.ToString($"D{digits}");
            _rows[i].FileName = $"{stem}{printIndex.ToString($"D{digits}")}{SelectedOutputExtension}";
        }

        RefreshOutputPaths();
    }

    private void RefreshOutputPaths()
    {
        if (_updating)
        {
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        foreach (var row in _rows)
        {
            row.Job.OutputPath = Path.Combine(directory, row.FileName);
        }
    }

    private void ApplyPrintSelectionToHighlightedRows(Row changedRow)
    {
        var targetRows = _pendingPrintToggleRows ?? HighlightedRows();
        _pendingPrintToggleRows = null;
        if (targetRows.Count <= 1 || !targetRows.Contains(changedRow))
        {
            return;
        }

        try
        {
            _updatingPrintSelection = true;
            // 多行高亮后点击“打印”勾选框时，以当前行状态为准批量同步，支持 Shift/Ctrl 选中后一次切换。
            foreach (var row in targetRows)
            {
                row.Selected = changedRow.Selected;
            }
        }
        finally
        {
            _updatingPrintSelection = false;
        }
    }

    private List<Row> HighlightedRows()
    {
        var rows = _grid.SelectedItems.OfType<Row>().Distinct().ToList();
        if (rows.Count == 0 && _grid.CurrentItem is Row current)
        {
            rows.Add(current);
        }
        return rows;
    }

    private void MarkHighlightedNotPrint()
    {
        foreach (var row in HighlightedRows())
        {
            row.Selected = false;
        }
        RemoveUnselectedRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void BatchChangeHighlightedPaper()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var rows = HighlightedRows();
        if (!TryGetCommonPaperOptions(rows, out var options))
        {
            MessageBox.Show("只有所选矩形框的候选纸张尺寸完全一致时，才能批量修改纸张。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SinglePlotPaperSelectionForm(options);
        if (CadDialog.ShowModal(dialog) != true)
        {
            return;
        }

        var selectedPaper = dialog.SelectedPaper;
        var paperChoice = PaperSizeDetector.FormatOption(selectedPaper);
        _suppressPaperEvents = true;
        try
        {
            foreach (var row in rows)
            {
                row.PaperChoice = paperChoice;
                ApplyPaper(row.Job, selectedPaper);
                row.RefreshFromJob();
            }
        }
        finally
        {
            _suppressPaperEvents = false;
        }

        RefreshFileNames();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private static bool TryGetCommonPaperOptions(IReadOnlyList<Row> rows, out IReadOnlyList<PaperDetection> options)
    {
        options = new PaperDetection[0];
        if (rows.Count == 0 || rows[0].Options.Count == 0)
        {
            return false;
        }

        var first = rows[0].Options;
        // 候选列表按下拉顺序逐项比较，保证用户选中的第 N 项对每个矩形框含义一致。
        foreach (var row in rows.Skip(1))
        {
            if (!HasSamePaperOptions(first, row.Options))
            {
                return false;
            }
        }

        options = first;
        return true;
    }

    private static bool HasSamePaperOptions(IReadOnlyList<PaperDetection> first, IReadOnlyList<PaperDetection> second)
    {
        if (first.Count != second.Count)
        {
            return false;
        }

        for (var i = 0; i < first.Count; i++)
        {
            if (!IsSamePaperOption(first[i], second[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSamePaperOption(PaperDetection first, PaperDetection second)
    {
        const double tolerance = 0.001;
        return string.Equals(first.PaperName, second.PaperName, StringComparison.Ordinal)
            && string.Equals(first.ScaleText, second.ScaleText, StringComparison.Ordinal)
            && Math.Abs(first.PaperWidthMm - second.PaperWidthMm) <= tolerance
            && Math.Abs(first.PaperHeightMm - second.PaperHeightMm) <= tolerance
            && Math.Abs(first.ScaleValue - second.ScaleValue) <= tolerance
            && first.IsLong == second.IsLong
            && first.RequiresCustomPaper == second.RequiresCustomPaper;
    }

    private void DeleteHighlighted()
    {
        foreach (var row in HighlightedRows())
        {
            _rows.Remove(row);
        }
        RefreshDisplayRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void RemoveUnselectedRows()
    {
        var removed = false;
        foreach (var row in _rows.Where(row => !row.Selected).ToList())
        {
            // 矩形框界面取消“打印”即表示从当前清单移除，避免列表编号和 CAD 红框编号不一致。
            _rows.Remove(row);
            removed = true;
        }

        if (removed)
        {
            RefreshDisplayRows();
        }
    }

    private void PrintOrStop()
    {
        if (_printCts != null)
        {
            // 正在打印中 → 停止
            _printCts.Cancel();
            return;
        }

        Print();
    }

    private void Print()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        RefreshOutputPaths();
        if (IsDwgOutput)
        {
            SplitDwgs();
            return;
        }

        var selected = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有勾选任何矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("请选择输出路径。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var device = SelectedDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show($"未找到可用的 {SelectedOutputFormat} 输出设备。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Directory.CreateDirectory(directory);
        SaveCurrentPlotOptions();
        ApplyLeaveMarginSelection(selected);
        var originalPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedOutput = Path.Combine(directory, SourceStem() + ".pdf");
        var mergePdf = IsPdfOutput && _mergePdf.IsChecked == true;
        var mergedOutputPaths = new List<string>();
        var completed = 0;
        var printLogLines = new List<string>();

        // 常规设置中的“生成打印日志”是全局日志总开关；关闭时只保留界面状态和错误提示，
        // 不在内存累计日志，也不触发日志目录或文件创建。
        void AppendPrintLog(string level, string message)
        {
            if (_settings.GeneratePrintLog)
            {
                printLogLines.Add(BatchPlotLogger.Format(level, message));
            }
        }

        string SavePrintLog()
        {
            return _settings.GeneratePrintLog
                ? BatchPlotLogger.SaveRunLog(printLogLines)
                : "";
        }

        static string BuildLogText(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "" : $"\n日志: {path}";
        }

        AppendPrintLog(
            "INFO",
            $"开始矩形框批量打印；共={selected.Count}；格式={SelectedOutputFormat}；设备={device}；打印样式={SelectedStyle()}");
        try
        {
            // 切换按钮为"停止"状态
            _printCts = new CancellationTokenSource();
            _printButton.Content = "停止";
            _printButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 40, 40));
            _printButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 30, 30));

            if (mergePdf)
            {
                temporaryDirectory = Path.Combine(Path.GetTempPath(), "ZwcadBatchPlot", "RectangleMerge_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                for (var i = 0; i < selected.Count; i++)
                {
                    selected[i].OutputPath = Path.Combine(temporaryDirectory, $"{i + 1:D5}.pdf");
                }
            }

            _status.Text = $"打印中... 0 / {selected.Count}";
            Pump();

            // 汇总本批所有任意加长尺寸后只更新一次实际 PMP，再进入连续打印。
            CustomPaperBatchPreparer.Prepare(selected, device);
            var results = PlotterService.PlotMany(
                selected, device, SelectedStyle(), _document, _settings,
                beforeJob: job =>
                {
                    completed++;
                    _status.Text = $"打印中... {completed} / {selected.Count}";
                    var finalOutput = originalPaths.TryGetValue(job, out var path) ? path : job.OutputPath;
                    AppendPrintLog(
                        "INFO",
                        $"开始打印 {completed}/{selected.Count}；源文件={job.SourceFile}；布局={job.SpaceName}；输出={finalOutput}");
                    Pump();
                },
                cancellationToken: _printCts.Token);

            foreach (var result in results)
            {
                var finalOutput = originalPaths.TryGetValue(result.Job, out var path)
                    ? path
                    : result.Job.OutputPath;
                AppendPrintLog(
                    result.Succeeded ? "INFO" : "ERROR",
                    result.Succeeded
                        ? $"打印成功；布局={result.Job.SpaceName}；输出={finalOutput}"
                        : $"打印失败；布局={result.Job.SpaceName}；输出={finalOutput}；错误={result.Error}");
            }

            var failures = results.Where(result => !result.Succeeded).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", failures.Select(result => result.Error?.Message)));
            }

            if (mergePdf)
            {
                _status.Text = "正在合并 PDF...";
                Pump();
                var mergeInputs = selected.Select(job => new PdfMergeInput(
                    job.OutputPath,
                    Path.GetFileNameWithoutExtension(originalPaths[job]),
                    OutputPaperNameResolver.Resolve(
                        job,
                        _settings.LongPaperSnapToleranceMm),
                    job.PaperWidthMm,
                    job.PaperHeightMm)).ToList();
                var mergePlans = PdfDocumentService.PlanMerges(
                    mergeInputs,
                    mergedOutput,
                    _settings.MergePdfByPaperSize,
                    _settings.AddSequenceWhenPdfExists);
                foreach (var mergePlan in mergePlans)
                {
                    PdfDocumentService.Merge(
                        mergePlan.Inputs,
                        mergePlan.OutputPath,
                        _settings.UseFileNameAsPdfBookmark);
                    mergedOutputPaths.Add(mergePlan.OutputPath);
                    AppendPrintLog("INFO", $"合并 PDF 成功；输出={mergePlan.OutputPath}");
                }
            }

            if (mergePdf && _settings.OpenMergedPdfAfterMerge)
            {
                OpenMergedPdfFiles(mergedOutputPaths);
            }
            else if (!mergePdf && _settings.OpenOutputDirectoryAfterBatchPrint)
            {
                RevealOutput(null, directory);
            }
            _status.Text = $"完成，共 {selected.Count} 张";
            AppendPrintLog("INFO", $"矩形框批量打印完成；成功={selected.Count}；失败=0");
            var printLogPath = SavePrintLog();
            var printLogText = BuildLogText(printLogPath);
            MessageBox.Show(
                mergePdf
                    ? $"打印并合并完成，共 {selected.Count} 张，生成 {mergedOutputPaths.Count} 个 PDF。\n{string.Join("\n", mergedOutputPaths)}{printLogText}"
                    : $"打印完成，共 {selected.Count} 张。\n{directory}{printLogText}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"已停止（已完成 {completed} / {selected.Count}）";
            AppendPrintLog("INFO", $"用户取消打印；已开始={completed}/{selected.Count}");
            var printLogPath = SavePrintLog();
            MessageBox.Show($"打印已停止。\n已完成 {completed} / {selected.Count} 张。{BuildLogText(printLogPath)}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "打印失败";
            AppendPrintLog("ERROR", "矩形框批量打印失败: " + ex);
            var printLogPath = SavePrintLog();
            MessageBox.Show("矩形框批量打印失败: " + ex.Message + BuildLogText(printLogPath), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _printCts?.Dispose();
            _printCts = null;
            // 恢复按钮
            _printButton.Content = "开始打印";
            _printButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
            _printButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 95, 170));

            foreach (var pair in originalPaths)
            {
                pair.Key.OutputPath = pair.Value;
            }
            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                try { Directory.Delete(temporaryDirectory, true); } catch { }
            }
            UpdateVisuals();
        }
    }

    private void SplitDwgs()
    {
        var selected = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有勾选任何矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("请选择输出路径。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"将按当前矩形框拆出 {selected.Count} 个 DWG 文件。\n\n输出位置：{directory}\n\n是否继续？",
            "矩形框批量拆图",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        SaveCurrentPlotOptions();
        Cursor = Cursors.Wait;
        IsEnabled = false;
        try
        {
            _status.Text = $"拆图中... 0 / {selected.Count}";
            var completed = 0;
            var explicitPaths = selected.ToDictionary(job => job, job => job.OutputPath);
            var results = DwgSplitService.SplitMany(
                selected,
                _document,
                _settings,
                beforeJob: _ =>
                {
                    completed++;
                    _status.Text = $"拆图中... {completed} / {selected.Count}";
                    Pump();
                },
                explicitOutputPaths: explicitPaths);

            var failures = results.Where(result => result.Error != null).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", failures.Select(result => result.Error?.Message)));
            }

            foreach (var result in results)
            {
                result.Job.OutputPath = result.OutputPath;
            }
            RevealOutput(null, directory);
            _status.Text = $"拆图完成，共 {selected.Count} 张";
            MessageBox.Show($"DWG 拆图完成，共 {selected.Count} 张。\n{directory}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "拆图失败";
            MessageBox.Show("矩形框批量拆图失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            Cursor = Cursors.Arrow;
            UpdateVisuals();
        }
    }

    private void ApplyLeaveMarginSelection(IEnumerable<PlotJob> jobs)
    {
        // PNG/JPG 使用像素介质，留白的毫米纸张/缩放语义不成立，作业层必须强制关闭。
        var leaveMargin = SupportsLeaveMargin && _leaveMargin.IsChecked == true;
        var marginMm = ReadMarginValue(_marginInput);
        foreach (var job in jobs)
        {
            // 留白是本次输出选项，预览和正式打印都写入同一个 PlotJob，保证效果一致。
            job.LeavePaperMargin = leaveMargin;
            job.PaperMarginMm = marginMm;
            // 负留白只缩比例，不能把图框扫描得到的任意纸张注册要求一并清除。
            job.RequiresCustomPaperRegistration =
                job.DetectedRequiresCustomPaperRegistration || (leaveMargin && marginMm > 0);
            if (!leaveMargin || marginMm <= 0)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }
        }
    }

    private void LoadPlotOptions()
    {
        _suppressComboEvents = true;
        _outputFormatCombo.Items.Clear();
        _outputFormatCombo.Items.Add("PDF");
        _outputFormatCombo.Items.Add("PNG");
        _outputFormatCombo.Items.Add("JPG");
        _outputFormatCombo.Items.Add("DWF");
        _outputFormatCombo.Items.Add("DWG");
        _outputFormatCombo.SelectedIndex = 0;
        RefreshSavePathModeOptions(preserveSelection: false);

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
        _pngPlotDevice = FindPlotDevice(
            devices,
            pngInstall.DeviceName,
            _ => false,
            AcadPlotterInstaller.PreferredPngPlotter);
        _jpgPlotDevice = FindPlotDevice(
            devices,
            jpgInstall.DeviceName,
            _ => false,
            AcadPlotterInstaller.PreferredJpgPlotter);
        _dwfPlotDevice = FindPlotDevice(
            devices,
            dwfInstall.DeviceName,
            value => value.IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                     && value.IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0,
            AcadPlotterInstaller.PreferredDwfPlotter,
            "DWF6 ePlot.pc3",
            "DWF6 ePlot.pc5",
            "ZWPLOT_DWF.pc5",
            "M_DWF.pc5");
        foreach (var style in PlotStyleManager.GetAvailableCtbStyles())
        {
            _style.Items.Add(style);
        }
        PlotStyleManager.RestoreSavedStyle(_style, _settings.LastStyleSheet);
        // 上次样式在当前 CAD 不可用时已回退；立刻写回设置，避免下次仍记住失效 CTB。
        SaveCurrentPlotOptions();
        UpdateOutputFormatUi();
        _suppressComboEvents = false;
        _styleSelectionReady = true;
    }

    private static string FindPlotDevice(
        IReadOnlyList<string> devices,
        string installedPlotter,
        Func<string, bool> fallbackPredicate,
        params string[] preferred)
    {
        foreach (var expected in new[] { installedPlotter }
                     .Concat(preferred)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var match = devices.FirstOrDefault(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        // 只能返回 CAD 当前会话已经枚举到的设备；磁盘上刚生成但未刷新到会话的名称不可直接用于 PlotSettings。
        return devices.FirstOrDefault(fallbackPredicate) ?? "";
    }

    private void SetAll(bool selected)
    {
        foreach (var row in _rows)
        {
            row.Selected = selected;
        }
        RefreshFileNames();
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        var selected = _rows.Count(row => row.Selected);
        var order = _settings.SortOrderHorizontalFirst ? "左→右、上→下" : "上→下、左→右";
        _status.Text = $"识别 {_rows.Count} 个矩形框  |  已选 {selected} 个  |  格式：{SelectedOutputFormat}  |  顺序：{order}  |  输出：{_outputDirectory.Text}";
        ScheduleOverlayIfRebuildNeeded();
    }

    /// <summary>
    /// 当前会画到 CAD 上的清单快照：顺序代表打印序号。
    /// </summary>
    private List<(PlotJob Job, string DrawingNumber)> CaptureOverlayRebuildKey()
    {
        var keys = new List<(PlotJob Job, string DrawingNumber)>();
        foreach (var row in _displayRows)
        {
            if (!row.Selected)
            {
                continue;
            }

            keys.Add((row.Job, row.Job.DrawingNumber ?? ""));
        }

        return keys;
    }

    /// <summary>
    /// 打印顺序（同一 Job 引用的先后）或图号任一变化，才需要整批 Show 红框。
    /// </summary>
    private static bool OverlayRebuildKeysEqual(
        IReadOnlyList<(PlotJob Job, string DrawingNumber)>? left,
        IReadOnlyList<(PlotJob Job, string DrawingNumber)>? right)
    {
        if (left == null || right == null)
        {
            return left == null && right == null;
        }

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

    /// <summary>
    /// 改纸张、切格式、刷状态栏时不要重建红框；只有框集合或序号变了才 Show。
    /// </summary>
    private void ScheduleOverlayIfRebuildNeeded()
    {
        var key = CaptureOverlayRebuildKey();
        if (_overlayPainted && OverlayRebuildKeysEqual(_lastOverlayRebuildKey, key))
        {
            return;
        }

        ScheduleOverlayRefresh();
    }

    /// <summary>
    /// 表格先刷新完，下一帧再画 CAD 红框，避免识别结果和 Regen 挤在同一次 UI 消息里卡住窗口。
    /// </summary>
    private void ScheduleOverlayRefresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        var generation = ++_overlayScheduleGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded || generation != _overlayScheduleGeneration)
            {
                return;
            }

            ShowOverlayNow();
        }));
    }

    private void ShowOverlayNow()
    {
        try
        {
            var selectedJobs = _displayRows.Where(row => row.Selected).Select(row => row.Job).ToList();
            var highlightJob = (_highlightedJobIndex >= 0 && _highlightedJobIndex < _rows.Count)
                ? _rows[_highlightedJobIndex].Job
                : null;
            _overlay.Show(selectedJobs, highlightJob);
            _lastOverlayRebuildKey = CaptureOverlayRebuildKey();
            _overlayPainted = true;
        }
        catch
        {
            _overlay.Clear(repaint: false);
            _lastOverlayRebuildKey = null;
            _overlayPainted = false;
        }
    }

    private void ChooseOutputDirectory()
    {
        // FolderBrowserDialog 保留 WinForms 版（WPF 没有等价物）。
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择输出目录",
            SelectedPath = _outputDirectory.Text
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _outputDirectoryIsCustom = true;
            SetOutputDirectoryText(dialog.SelectedPath);
            SaveCurrentPlotOptions();
            RefreshOutputPaths();
        }
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
            SetOutputDirectoryText(directory);
        }

        SaveCurrentPlotOptions();
        RefreshOutputPaths();
    }

    private void RefreshSavePathModeOptions(bool preserveSelection)
    {
        var selectedIndex = preserveSelection && _savePathModeCombo.SelectedIndex >= 0
            ? Math.Min(_savePathModeCombo.SelectedIndex, 1)
            : 0;
        var format = SelectedOutputFormat;
        var formatPathText = string.IsNullOrWhiteSpace(format)
            ? "源文件路径/输出格式"
            : "源文件路径/" + format;

        var wasSuppressing = _suppressComboEvents;
        _suppressComboEvents = true;
        try
        {
            _savePathModeCombo.Items.Clear();
            _savePathModeCombo.Items.Add("源文件路径");
            _savePathModeCombo.Items.Add(formatPathText);
            _savePathModeCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _suppressComboEvents = wasSuppressing;
        }
    }

    private void ApplySelectedSavePathMode()
    {
        _outputDirectoryIsCustom = false;
        UpdateAutomaticOutputDirectory();
        SaveCurrentPlotOptions();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private void UpdateAutomaticOutputDirectory()
    {
        var subfolder = AutomaticOutputSubfolder;
        SetOutputDirectoryText(string.IsNullOrWhiteSpace(subfolder)
            ? SourceDirectory()
            : Path.Combine(SourceDirectory(), subfolder));
    }

    private void SetOutputDirectoryText(string text)
    {
        _suppressTextEvents = true;
        try
        {
            _outputDirectory.Text = text;
        }
        finally
        {
            _suppressTextEvents = false;
        }
        _outputDirectoryModified = false;
    }

    private void UpdateOutputFormatUi()
    {
        RefreshSavePathModeOptions(preserveSelection: true);
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var plotOutput = !IsDwgOutput;
        _style.IsEnabled = plotOutput;
        _styleSettingsButton.IsEnabled = plotOutput && _style.SelectedIndex >= 0;
        // 禁用但保留勾选状态，切回 PDF/DWF 后恢复用户原选择；实际作业另有强制关闭保护。
        _leaveMargin.IsEnabled = SupportsLeaveMargin;
        _marginInput.IsEnabled = SupportsLeaveMargin && _leaveMargin.IsChecked == true;
        _mergePdf.IsEnabled = IsPdfOutput;
        RefreshFileNames();
        SaveCurrentPlotOptions();
        UpdateVisuals();
    }

    private string SourceDirectory()
    {
        var file = _document.Database.Filename;
        return string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private string SourceStem()
    {
        var value = Path.GetFileNameWithoutExtension(_document.Database.Filename);
        return string.IsNullOrWhiteSpace(value) ? Path.GetFileNameWithoutExtension(_document.Name) : value;
    }

    private string SelectedOutputFormat => _outputFormatCombo.SelectedItem?.ToString()?.Trim() ?? "PDF";
    private string SelectedOutputExtension => "." + SelectedOutputFormat.ToLowerInvariant();
    private bool IsPdfOutput => string.Equals(SelectedOutputFormat, "PDF", StringComparison.OrdinalIgnoreCase);
    private bool IsDwgOutput => string.Equals(SelectedOutputFormat, "DWG", StringComparison.OrdinalIgnoreCase);
    private bool IsPngOutput => string.Equals(SelectedOutputFormat, "PNG", StringComparison.OrdinalIgnoreCase);
    private bool IsJpgOutput => string.Equals(SelectedOutputFormat, "JPG", StringComparison.OrdinalIgnoreCase);
    private bool IsDwfOutput => string.Equals(SelectedOutputFormat, "DWF", StringComparison.OrdinalIgnoreCase);
    private bool SupportsLeaveMargin => !IsDwgOutput && !IsPngOutput && !IsJpgOutput;
    private string? AutomaticOutputSubfolder => _savePathModeCombo.SelectedIndex == 1
        ? FileNameSanitizer.Clean(SelectedOutputFormat)
        : null;
    private string SelectedDevice() => IsPdfOutput
        ? AcadPlotterInstaller.PreferredPdfPlotter
        : IsJpgOutput ? _jpgPlotDevice
        : IsDwfOutput ? _dwfPlotDevice
        : _pngPlotDevice;
    private string SelectedStyle() => _style.SelectedItem?.ToString() ?? "";

    private void SaveCurrentPlotOptions()
    {
        _settings.LastPlotDevice = AcadPlotterInstaller.PreferredPdfPlotter;
        var style = PlotStyleManager.NormalizeStyleName(SelectedStyle());
        if (!string.IsNullOrEmpty(style))
        {
            _settings.LastStyleSheet = style;
        }
        _settings.MergePdf = _mergePdf.IsChecked == true;
        _settings.LeavePaperMargin = _leaveMargin.IsChecked == true;
        _settings.PaperMarginMm = ReadMarginValue(_marginInput);
        AppSettingsStore.Save(_settings);
    }

    /// <summary>初始化留白下拉列表，正值=扩大纸张，负值=缩比例，整数1~10配对显示。</summary>
    private static void InitMarginCombo(ComboBox combo, double savedValue)
    {
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
    private static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;

    private static void ApplyPaper(PlotJob job, PaperDetection paper)
    {
        job.PaperName = paper.PaperName;
        job.PaperWidthMm = paper.PaperWidthMm;
        job.PaperHeightMm = paper.PaperHeightMm;
        job.PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm";
        job.ScaleText = paper.ScaleText;
        job.DetectedRequiresCustomPaperRegistration = paper.RequiresCustomPaper;
        job.RequiresCustomPaperRegistration = paper.RequiresCustomPaper;
        // 用户从任意纸切回标准/模数纸时，立即清除上一次预览留下的严格动态纸张状态。
        if (!paper.RequiresCustomPaper)
        {
            job.RequireExactPaperSize = false;
            job.UseExactWindowScale = false;
            job.CustomPaperWasAdded = false;
        }
    }

    private static void RevealOutput(string? file, string directory)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = !string.IsNullOrWhiteSpace(file) && File.Exists(file)
                    ? $"/select,\"{Path.GetFullPath(file)}\""
                    : $"\"{Path.GetFullPath(directory)}\"",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static void OpenMergedPdfFiles(IEnumerable<string> outputFiles)
    {
        foreach (var outputFile in outputFiles.Where(File.Exists))
        {
            try
            {
                // 分组后的每个合并 PDF 都直接打开，行为与单一合并文件保持一致。
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.GetFullPath(outputFile),
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    private void ShowSortSettings()
    {
        // 矩形框没有图号/图名业务字段，始终只允许选择图纸位置的排列方向。
        var dialog = new SortOrderDialog(
            _settings.SortOrderHorizontalFirst,
            showSortBasis: false,
            sortMode: TitleBlockSortMode.Spatial);
        if (CadDialog.ShowModal(dialog) != true) return;
        _settings.SortOrderHorizontalFirst = dialog.HorizontalFirst;
        AppSettingsStore.Save(_settings);
        SortRows();
    }

    private void ShowSettingsAtTab(int tabIndex)
    {
        while (true)
        {
            SettingsForm.InitialTabIndex = tabIndex;
            var form = new SettingsForm();
            if (CadDialog.ShowModal(form) != true)
            {
                break;
            }

            // 重新加载相关设置
            var updated = AppSettingsStore.Load();
            var recognitionSettingsChanged =
                Math.Abs(_settings.PaperMatchToleranceMm - updated.PaperMatchToleranceMm) > 1e-9
                || _settings.RecognizeFourLineRectangleFrames
                != updated.RecognizeFourLineRectangleFrames;
            _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
            _settings.RecognizeFourLineRectangleFrames = updated.RecognizeFourLineRectangleFrames;
            _settings.HideFrameBoundaryWhenPlotting = updated.HideFrameBoundaryWhenPlotting;
            _settings.PlotTransparency = updated.PlotTransparency;
            _settings.GeneratePrintLog = updated.GeneratePrintLog;
            _settings.ConvertTextToGeometryWhenPlotting = updated.ConvertTextToGeometryWhenPlotting;
            _settings.LongPaperSnapToleranceMm = updated.LongPaperSnapToleranceMm;
            _settings.LongPaperNameFormat = updated.LongPaperNameFormat;
            _settings.SortOrderHorizontalFirst = updated.SortOrderHorizontalFirst;

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                if (recognitionSettingsChanged)
                {
                    // 开关或纸张容差变化后立即沿用上次扫描范围重扫，避免列表仍显示旧识别结果。
                    ReloadFrames();
                }
                return;
            }

            tabIndex = form.SelectedTabIndex;
            Hide();
            Pump();
            try
            {
                var document = GetActiveCadDocument();
                if (document == null)
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
                if (form.RequestPickDirectoryTextAppearance)
                {
                    ok = DirectoryTableGenerator.PromptTextAppearance(document, settings, out _, out message);
                }
                else if (form.RequestPickDirectoryRowHeight)
                {
                    ok = DirectoryTableGenerator.PromptRowHeight(document, settings, out _, out message);
                }
                else
                {
                    ok = DirectoryTableGenerator.PromptColumnSize(
                        document,
                        settings,
                        form.RequestedDirectoryColumnKey ?? "",
                        out _,
                        out message);
                }

                MessageBox.Show(
                    message,
                    "批量打印设置",
                    MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            finally
            {
                Show();
                Activate();
            }
        }
    }

    private static void Pump()
    {
        // 等价原 WinForms Application.DoEvents：在打印循环里刷新界面。
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
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
}
