using System;
using System.Threading;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// 通用型批量打印面板（WPF 版，非模态窗口；由 BatchPlotCommands 通过 CadDialog.ShowModeless 显示）。
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

        /// <summary>扫描识别到的图号；无属性识别时为空。</summary>
        public string DrawingNumber => Job.DrawingNumber ?? "";

        /// <summary>扫描识别到的图名；无属性识别时为空或不显示列。</summary>
        public string Title => Job.Title ?? "";

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
            OnPropertyChanged(nameof(DrawingNumber));
            OnPropertyChanged(nameof(Title));
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
    private List<ObjectId>? _scanSelectionIds;
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
    private string _dwfPlotDevice = "";
    private bool _styleSelectionReady;
    /// <summary>本批是否识别到至少一张图号或图名属性；决定是否显示列并走设置文件名规则。</summary>
    private bool _hasAttributeIdentity;
    private List<(PlotJob Job, string DrawingNumber)>? _lastOverlayRebuildKey;
    private bool _overlayPainted;
    /// <summary>多文件批打进列表后禁止 CAD 临时红框/序号，直至再次扫描当前图、框选或清空清单。</summary>
    private bool _suppressSequenceOverlayFromMultiFile;

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
        Closing += (_, _) =>
        {
            // 关闭时若仍在打印，先请求取消，避免后台继续跑。
            _printCts?.Cancel();
        };
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

    private void ScanSelectedObjects_Click(object sender, RoutedEventArgs e) => ScanSelectedObjects();

    private void AddDwgFiles_Click(object sender, RoutedEventArgs e) => AddDwgFiles();
    private void AddDwgFolder_Click(object sender, RoutedEventArgs e) => AddDwgFolder();

    private void ReloadFrames_Click(object sender, RoutedEventArgs e) => ReloadFrames();

    private void ClearRows_Click(object sender, RoutedEventArgs e) => ClearRows();

    private void ClearRows()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (_rows.Count == 0 && _displayRows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("确定清空当前矩形框清单吗？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        ReplaceBindingListContents(_rows, Array.Empty<Row>());
        ReplaceBindingListContents(_displayRows, Array.Empty<Row>());
        _scanSelectionIds = null;
        _lastScanScope = null;
        _hasAttributeIdentity = false;
        UpdateAttributeIdentityColumns();
        _viewSortedByHeader = false;
        _sortMemberPath = "";
        try
        {
            _overlay.Clear();
        }
        catch
        {
        }
    }



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

    private void ScaleSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(3);

    private void GeneralSettings_Click(object sender, RoutedEventArgs e) => ShowSettingsAtTab(0);

        private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // 原列头点击排序（Programmatic SortMode）。模板列在部分宿主下不触发本事件，见 PreviewMouse 兜底。
        e.Handled = true;
        ApplyHeaderSort(e.Column);
    }

    /// <summary>
    /// 模板列表头点击兜底：WPF DataGridTemplateColumn 有时不触发 Sorting，点「纸张尺寸 / 比例」会无反应。
    /// </summary>
    private bool TryHandleColumnHeaderSortClick(DependencyObject? source)
    {
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column == null)
        {
            return false;
        }

        ApplyHeaderSort(header.Column);
        return true;
    }

    private void ApplyHeaderSort(DataGridColumn column)
    {
        var columnIndex = _grid.Columns.IndexOf(column);
        if (columnIndex < 0 || columnIndex >= _grid.Columns.Count)
        {
            return;
        }

        var memberPath = column.SortMemberPath ?? "";
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

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        var current = source;
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private string GetHeaderSortValue(Row row, string memberPath)
    {
        switch (memberPath)
        {
            case nameof(Row.Selected):
                return row.Selected ? "1" : "0";
            case nameof(Row.FileName):
                return row.FileName;
            case nameof(Row.DrawingNumber):
                return row.DrawingNumber;
            case nameof(Row.Title):
                return row.Title;
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
        if (TryHandleColumnHeaderSortClick(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

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
        // 候选完全一致，或均可按同一套长宽比图幅名解释时，允许批量改纸。
        _changePaperItem.IsEnabled = CanBatchChangePaper(HighlightedRows());
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

        // 红框/序号在不打印层，预览不必取消表格选中或改 CAD 高亮。
        // 非模态按钮处于应用上下文；PlotEngine 交互预览必须进命令上下文，否则会「假启动」。
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

            PendingPlotPreview.Start(new PendingPlotPreview.Request
            {
                Job = row.Job,
                DeviceName = device,
                StyleSheet = PlotStyleManager.ResolveJobStyle(row.Job, SelectedStyle()),
                Document = _document,
                OnFinally = () => CadWindowFocus.RestoreDialog(this),
                OnError = ex => MessageBox.Show(
                    "打印预览失败: " + ex.Message,
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error)
            }, Dispatcher);
        }
        catch (Exception ex)
        {
            PendingPlotPreview.Take();
            MessageBox.Show("打印预览失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private void BatchChangePaper_Click(object sender, RoutedEventArgs e) => BatchChangeHighlightedPaper();

    private void MarkNotPrint_Click(object sender, RoutedEventArgs e) => MarkHighlightedNotPrint();

    private void DeleteHighlighted_Click(object sender, RoutedEventArgs e) => DeleteHighlighted();

    // ── 数据加载 ──

    private void LoadRows(IReadOnlyList<RectangleFrameScanner.Result> results, bool append = false)
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

        if (append)
        {
            var existingKeys = new HashSet<string>(_rows.Select(row => PlotJobIdentityKey(row.Job)), StringComparer.OrdinalIgnoreCase);
            var merged = _rows.ToList();
            foreach (var row in rows)
            {
                if (existingKeys.Add(PlotJobIdentityKey(row.Job)))
                {
                    merged.Add(row);
                }
            }

            rows = merged;
        }

        _hasAttributeIdentity = rows.Any(row =>
            !string.IsNullOrWhiteSpace(row.Job.CadDrawingNumber)
            || !string.IsNullOrWhiteSpace(row.Job.CadTitle));
        if (_hasAttributeIdentity)
        {
            // 进入属性命名模式后，图号/图名只保留识别结果，避免文件名规则混入 DWG 名或序号。
            foreach (var row in rows)
            {
                row.Job.DrawingNumber = row.Job.CadDrawingNumber ?? "";
                row.Job.Title = row.Job.CadTitle ?? "";
            }
        }

        UpdateAttributeIdentityColumns();
        ReplaceBindingListContents(_rows, rows);
        _viewSortedByHeader = false;
        _sortMemberPath = "";
        SortRows();
    }

    private static string PlotJobIdentityKey(PlotJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.BlockHandle))
        {
            return $"H|{job.SourceFile}|{job.SpaceName}|{job.BlockHandle}";
        }

        return $"G|{job.SourceFile}|{job.SpaceName}|{job.MinX:0.###}|{job.MinY:0.###}|{job.MaxX:0.###}|{job.MaxY:0.###}";
    }

    /// <summary>有识别结果时显示图号/图名列，否则保持原有列布局。</summary>
    private void UpdateAttributeIdentityColumns()
    {
        var visibility = _hasAttributeIdentity
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        _drawingNumberColumn.Visibility = visibility;
        _titleColumn.Visibility = visibility;
    }

    private void RefreshFileNames()
    {
        if (_hasAttributeIdentity)
        {
            RefreshAttributeIdentityFileNames();
            return;
        }

        var fallbackStem = GetDocumentFileStem();
        var digits = Math.Max(1, Math.Min(10, _settings.FileNameSequenceDigits));
        var printIndex = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Selected)
            {
                continue;
            }

            printIndex++;
            var stem = GetJobFileStem(_rows[i].Job, fallbackStem);
            _rows[i].Job.DrawingNumber = printIndex.ToString($"D{digits}");
            _rows[i].FileName = $"{stem}{printIndex.ToString($"D{digits}")}{SelectedOutputExtension}";
            _rows[i].RefreshFromJob();
        }

        RefreshOutputPaths();
    }

    /// <summary>
    /// 本批有属性识别时：有图号/图名的行走设置文件名规则；
    /// 无属性的行仍按原规则 {DWG名}{序号}，避免出现「_.pdf」这类空占位结果。
    /// </summary>
    private void RefreshAttributeIdentityFileNames()
    {
        var stem = GetDocumentFileStem();
        var legacyDigits = Math.Max(1, Math.Min(10, _settings.FileNameSequenceDigits));
        var selectedCount = _rows.Count(row => row.Selected);
        var patternDigits = FileNameSanitizer.ResolveSequenceDigits(
            _settings.AutoFileNameSequenceDigits,
            _settings.FileNameSequenceDigits,
            _settings.FileNameSequenceStartNumber,
            selectedCount);
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = _outputDirectory.Text.Trim();
        var selectedIndex = 0;
        var printIndex = 0;
        foreach (var row in _rows)
        {
            if (!row.Selected)
            {
                continue;
            }

            var sequenceNumber = _settings.FileNameSequenceStartNumber + selectedIndex;
            selectedIndex++;
            printIndex++;

            string baseName;
            if (RowHasAttributeIdentity(row))
            {
                baseName = FileNameSanitizer.FormatFileNamePattern(
                    _settings.PdfFileNamePattern,
                    row.Job,
                    sequenceNumber,
                    patternDigits,
                    _settings.LongPaperNameFormat,
                    _settings.LongPaperSnapToleranceMm);
            }
            else
            {
                // 该框未识别到图号/图名：与整批无属性时一致，用 DWG 名 + 勾选序号。
                var jobStem = GetJobFileStem(row.Job, stem);
                baseName = $"{jobStem}{printIndex.ToString($"D{legacyDigits}")}";
            }

            var fullPath = FileNameSanitizer.MakeUnique(
                string.IsNullOrWhiteSpace(directory) ? "." : directory,
                baseName,
                reservedPaths,
                _settings.AddSequenceWhenPdfExists,
                SelectedOutputExtension,
                createDirectory: false);
            reservedPaths.Add(fullPath);
            row.FileName = Path.GetFileName(fullPath);
            row.RefreshFromJob();
        }

        RefreshOutputPaths();
    }

    /// <summary>当前文档用于默认文件名的主文件名（无扩展名）。</summary>
    private string GetDocumentFileStem()
    {
        var stem = Path.GetFileNameWithoutExtension(_document.Database.Filename);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = Path.GetFileNameWithoutExtension(_document.Name);
        }

        return stem ?? "";
    }

    /// <summary>多文件批打时优先用任务 SourceFile 作为文件名主干。</summary>
    private static string GetJobFileStem(PlotJob job, string fallbackStem)
    {
        if (!string.IsNullOrWhiteSpace(job.SourceFile))
        {
            var stem = Path.GetFileNameWithoutExtension(job.SourceFile);
            if (!string.IsNullOrWhiteSpace(stem))
            {
                return stem;
            }
        }

        return fallbackStem ?? "";
    }

    /// <summary>该行是否识别到非空图号或图名属性。</summary>
    private static bool RowHasAttributeIdentity(Row row)
        => !string.IsNullOrWhiteSpace(row.Job.CadDrawingNumber)
           || !string.IsNullOrWhiteSpace(row.Job.CadTitle);

    private void ReloadFrames()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        try
        {
            List<RectangleFrameScanner.Result> results;
            if (_lastScanScope.HasValue)
            {
                results = ScanScopeWithProgress(_lastScanScope.Value);
            }
            else if (_scanSelectionIds != null)
            {
                results = ScanSelectionWithProgress(_scanSelectionIds);
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
        catch (OperationCanceledException)
        {
            MessageBox.Show("已取消识别。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("重新识别矩形框失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope(this);

    /// <summary>
    /// 扫描失败时把完整异常给用户看（弹窗 + 剪贴板 + 日志），避免只剩 eNotApplicable 无法反馈。
    /// </summary>
    private void ShowScanFailure(string action, Exception ex)
    {
        var detail = ex.ToString();
        try
        {
            _document.Editor.WriteMessage("\n" + action + "\n" + detail + "\n");
        }
        catch
        {
        }

        var logPath = "";
        try
        {
            Directory.CreateDirectory(BatchPlotLogger.LogDirectory);
            logPath = Path.Combine(
                BatchPlotLogger.LogDirectory,
                "ScanError_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
            File.WriteAllText(logPath, action + Environment.NewLine + detail, Encoding.UTF8);
        }
        catch
        {
            logPath = "";
        }

        try
        {
            System.Windows.Clipboard.SetText(detail);
        }
        catch
        {
        }

        var body = action + "\n\n" + detail;
        body += string.IsNullOrWhiteSpace(logPath)
            ? "\n\n完整内容已尝试复制到剪贴板。"
            : "\n\n完整内容已复制到剪贴板，并写入日志:\n" + logPath;

        const int maxChars = 6000;
        if (body.Length > maxChars)
        {
            body = body.Substring(0, maxChars) + "\n…(已截断，完整内容见剪贴板/日志)";
        }

        MessageBox.Show(body, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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

        var files = dialog.FileNames
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        BeginMultiFileBatch(files);
    }

    /// <summary>选择文件夹，收集其中（含子文件夹）全部 DWG，后续流程与多文件批打相同。</summary>
    private void AddDwgFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择包含 DWG 的文件夹（将包含子文件夹内的文件）",
            ShowNewFolderButton = false
        };
        var seed = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(seed) || !Directory.Exists(seed))
        {
            seed = SourceDirectory();
        }

        if (!string.IsNullOrWhiteSpace(seed) && Directory.Exists(seed))
        {
            dialog.SelectedPath = seed;
        }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK
            || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        List<string> files;
        try
        {
            files = CollectDwgFilesInFolder(dialog.SelectedPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "读取文件夹失败: " + ex.Message,
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (files.Count == 0)
        {
            MessageBox.Show(
                "该文件夹内未找到 DWG 文件。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        BeginMultiFileBatch(files);
    }

    /// <summary>枚举文件夹及其子文件夹中的全部 .dwg（忽略大小写去重，按路径排序）。</summary>
    private static List<string> CollectDwgFilesInFolder(string folder)
    {
        return Directory.EnumerateFiles(folder, "*.dwg", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>多文件批打核心：枚举空间 → 勾选 → 扫描入清单（文件选择与文件夹选择共用）。</summary>
    private void BeginMultiFileBatch(IReadOnlyList<string> files)
    {
        if (files == null || files.Count == 0)
        {
            return;
        }

        var catalogErrors = new List<string>();
        var spaces = DwgSpaceCatalog.ListSpaces(files, catalogErrors);
        if (catalogErrors.Count > 0)
        {
            // 通用型无统一日志窗，失败详情放提示框。
        }

        if (spaces.Count == 0)
        {
            MessageBox.Show(
                catalogErrors.Count > 0
                    ? "未能枚举到可扫描的模型/布局。\n" + string.Join("\n", catalogErrors)
                    : "所选 DWG 中没有可扫描的模型或布局。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var defaultStyle = SelectedStyle();
        var picker = new ScanSpacePickerDialog(
            spaces,
            defaultStyle,
            PlotStyleManager.GetAvailableCtbStyles());
        if (CadDialog.ShowModal(picker, this) != true)
        {
            return;
        }

        var selected = picker.SelectedSpaces;
        if (selected.Count == 0)
        {
            return;
        }

        // 确认后先清空清单，再只扫勾选空间。
        ReplaceBindingListContents(_rows, Array.Empty<Row>());
        ReplaceBindingListContents(_displayRows, Array.Empty<Row>());
        ClearSequenceOverlay();
        _lastScanScope = null;
        _scanSelectionIds = null;
        _hasAttributeIdentity = false;
        UpdateAttributeIdentityColumns();

        var allResults = new List<RectangleFrameScanner.Result>();
        var errors = new List<string>();
        string currentPath;
        try
        {
            currentPath = string.IsNullOrWhiteSpace(_document.Database.Filename)
                ? ""
                : Path.GetFullPath(_document.Database.Filename);
        }
        catch
        {
            currentPath = _document.Database.Filename ?? "";
        }

        foreach (var fileGroup in selected.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var file = fileGroup.Key;
            var allowed = new HashSet<string>(
                fileGroup.Select(s => s.LayoutName),
                StringComparer.OrdinalIgnoreCase);
            try
            {
                var isCurrent = !string.IsNullOrWhiteSpace(currentPath)
                    && string.Equals(Path.GetFullPath(file), currentPath, StringComparison.OrdinalIgnoreCase);
                List<RectangleFrameScanner.Result> results;
                if (isCurrent)
                {
                    results = RectangleFrameScanner.ScanScope(
                        _document,
                        TitleBlockScanScope.AllSpaces,
                        _settings.PaperMatchToleranceMm,
                        _settings.RecognizeFourLineRectangleFrames,
                        progress: null,
                        cancellationToken: default,
                        allowedLayoutNames: allowed);
                    TransformResultsToDcs(results);
                }
                else
                {
                    using var db = new Database(false, true);
                    db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
                    db.CloseInput(true);
                    results = RectangleFrameScanner.ScanDatabase(
                        db,
                        file,
                        TitleBlockScanScope.AllSpaces,
                        allowed,
                        _settings.PaperMatchToleranceMm,
                        _settings.RecognizeFourLineRectangleFrames);
                }

                var fileStyle = fileGroup.FirstOrDefault()?.StyleSheet ?? "";
                foreach (var result in results)
                {
                    result.Job.StyleSheet = fileStyle;
                }

                allResults.AddRange(results);
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }

        if (allResults.Count == 0)
        {
            LoadRows(allResults);
            _suppressSequenceOverlayFromMultiFile = true;
            ClearSequenceOverlay();
            MessageBox.Show(
                errors.Count > 0
                    ? "扫描完成但未识别到矩形框。\n" + string.Join("\n", errors)
                    : "勾选空间内没有识别到符合常见纸张比例的矩形框。",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        LoadRows(allResults);
        // 多文件批打识别结果一律不画临时红框和数字（即使勾选的是当前图）。
        _suppressSequenceOverlayFromMultiFile = true;
        ClearSequenceOverlay();

        if (errors.Count > 0)
        {
            MessageBox.Show(
                "部分 DWG 扫描失败:\n" + string.Join("\n", errors),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private bool IsCurrentDocumentSource(string sourceFile)
    {
        if (string.IsNullOrWhiteSpace(sourceFile))
        {
            return false;
        }

        try
        {
            var current = _document.Database.Filename;
            if (string.IsNullOrWhiteSpace(current))
            {
                return string.Equals(sourceFile, _document.Name, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(Path.GetFullPath(sourceFile), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(sourceFile, _document.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ClearSequenceOverlay()
    {
        _overlayScheduleGeneration++;
        _overlay.Clear(repaint: false);
        _overlayPainted = false;
        _lastOverlayRebuildKey = null;
    }

    /// <summary>
    /// 扫描当前图：弹出范围对话框后按所选空间识别矩形图框。
    /// </summary>
    private void ScanCurrentDrawing()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        var scope = PromptScanScope();
        if (scope == null)
        {
            return;
        }

        try
        {
            var results = ScanScopeWithProgress(scope.Value);
            if (results.Count == 0)
            {
                MessageBox.Show("扫描范围内没有识别到符合常见纸张比例的矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            _lastScanScope = scope;
            _scanSelectionIds = null;
            LoadRows(results);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("已取消识别。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("扫描当前图失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 框选扫描：先按类型过滤选择对象，再识别选中矩形图框。
    /// 未拾取时右键弹出扫描范围菜单；框选与右键范围结果累加到现有清单（按几何/句柄去重）；取消选择时保持现有清单不变。
    /// </summary>
    private void ScanSelectedObjects()
    {
        _suppressSequenceOverlayFromMultiFile = false;
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var prompt = ObjectSelectionPrompt.Prompt(
                _document.Editor,
                "\n选择要批量打印的矩形图框对象(右键选择扫描范围): ",
                ObjectSelectionPrompt.RectangleFrameFilter(_settings.RecognizeFourLineRectangleFrames));
            if (prompt.Cancelled)
            {
                return;
            }

            List<RectangleFrameScanner.Result> results;
            if (prompt.Scope is { } scope)
            {
                results = ScanScopeWithProgress(scope);
                if (results.Count == 0)
                {
                    MessageBox.Show("扫描范围内没有识别到符合常见纸张比例的矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                TransformResultsToDcs(results);
                _lastScanScope = null;
                _scanSelectionIds = null;
                LoadRows(results, append: true);
                return;
            }

            if (prompt.SelectedIds is not { Length: > 0 } selectedIds)
            {
                return;
            }

            results = ScanSelectionWithProgress(selectedIds);
            if (results.Count == 0)
            {
                MessageBox.Show("选中对象内没有识别到符合常见纸张比例的矩形框。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TransformResultsToDcs(results);
            if (_scanSelectionIds == null)
            {
                _scanSelectionIds = selectedIds.ToList();
            }
            else
            {
                var known = new HashSet<ObjectId>(_scanSelectionIds);
                foreach (var id in selectedIds)
                {
                    if (known.Add(id))
                    {
                        _scanSelectionIds.Add(id);
                    }
                }
            }

            _lastScanScope = null;
            LoadRows(results, append: true);
        }
        catch (OperationCanceledException)
        {
            MessageBox.Show("已取消识别。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
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

    /// <summary>带进度窗执行范围扫描；用户取消时抛出 <see cref="OperationCanceledException"/>。</summary>
    private List<RectangleFrameScanner.Result> ScanScopeWithProgress(TitleBlockScanScope scope)
    {
        using var session = RectangleScanProgressSession.Start(this);
        return RectangleFrameScanner.ScanScope(
            _document,
            scope,
            _settings.PaperMatchToleranceMm,
            _settings.RecognizeFourLineRectangleFrames,
            session.Progress,
            session.Token);
    }

    /// <summary>带进度窗执行对象选择扫描。</summary>
    private List<RectangleFrameScanner.Result> ScanSelectionWithProgress(IEnumerable<ObjectId> selectedIds)
    {
        using var session = RectangleScanProgressSession.Start(this);
        return RectangleFrameScanner.ScanSelection(
            _document,
            selectedIds,
            _settings.PaperMatchToleranceMm,
            _settings.RecognizeFourLineRectangleFrames,
            session.Progress,
            session.Token);
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

                // 图纸空间打印窗口就是纸面坐标；禁止用模型空间当前视图矩阵污染布局图框。
                if (job.IsPaperSpace)
                {
                    if (result.CornerPoints != null)
                    {
                        job.CornerPoints = (double[])result.CornerPoints.Clone();
                    }

                    job.IsDcsWindow = true;
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

        var allRows = _rows.ToList();
        var sortedRows = SortRectangleRows(allRows);

        ReplaceBindingListContents(_rows, sortedRows);
        RefreshDisplayRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    /// <summary>
    /// 通用型排序：多文件时先按文件名；同一文件内有识别图号则按图号，否则与单文件批打相同（布局 + 空间位置）。
    /// </summary>
    private List<Row> SortRectangleRows(IReadOnlyList<Row> rows)
    {
        var result = new List<Row>(rows.Count);
        var sourceGroups = rows
            .Select((row, index) => new { Row = row, Index = index })
            .GroupBy(item => GetSourceGroupKey(item.Row.Job.SourceFile), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => GetSourceFileSortName(group.Key), NaturalStringComparer.Instance)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Min(item => item.Index));

        foreach (var sourceGroup in sourceGroups)
        {
            var fileRows = sourceGroup.Select(item => item.Row).ToList();
            result.AddRange(SortRowsWithinSourceFile(fileRows));
        }

        return result;
    }

    /// <summary>
    /// 单文件内排序：有识别图号（属性身份模式）时按图号自然序，同图号再用单文件空间序打断平局；
    /// 否则直接按单文件打印顺序（SpatialSorter.SortByLayout）。
    /// </summary>
    private List<Row> SortRowsWithinSourceFile(List<Row> fileRows)
    {
        if (fileRows.Count <= 1)
        {
            return fileRows;
        }

        var horizontalFirst = _settings.SortOrderHorizontalFirst;
        var jobToRow = fileRows.ToDictionary(row => row.Job, row => row);

        if (_hasAttributeIdentity)
        {
            var byDrawingNumber = fileRows
                .OrderBy(row => row.Job.DrawingNumber ?? "", NaturalStringComparer.Instance)
                .ThenBy(row => row.Job.Title ?? "", StringComparer.CurrentCultureIgnoreCase)
                .Select(row => row.Job)
                .ToList();

            return SpatiallyBreakTiesWithinFile(byDrawingNumber)
                .Select(job => jobToRow[job])
                .ToList();
        }

        return SpatialSorter.SortByLayout(fileRows.Select(row => row.Job).ToList(), horizontalFirst)
            .Select(job => jobToRow[job])
            .ToList();
    }

    /// <summary>
    /// 图号（及图名）完全相同的连续段内，按单文件空间顺序二次排序。
    /// </summary>
    private List<PlotJob> SpatiallyBreakTiesWithinFile(List<PlotJob> sortedJobs)
    {
        if (sortedJobs.Count <= 1)
        {
            return sortedJobs;
        }

        var horizontalFirst = _settings.SortOrderHorizontalFirst;
        var result = new List<PlotJob>(sortedJobs.Count);
        var i = 0;
        while (i < sortedJobs.Count)
        {
            var anchor = sortedJobs[i];
            var j = i + 1;
            while (j < sortedJobs.Count
                && NaturalStringComparer.Instance.Compare(sortedJobs[j].DrawingNumber, anchor.DrawingNumber) == 0
                && string.Equals(sortedJobs[j].Title, anchor.Title, StringComparison.CurrentCultureIgnoreCase))
            {
                j++;
            }

            var group = sortedJobs.GetRange(i, j - i);
            if (group.Count > 1)
            {
                group = SpatialSorter.SortByLayout(group, horizontalFirst);
            }

            result.AddRange(group);
            i = j;
        }

        return result;
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
            return sourceFile!.Trim();
        }
    }

    private static string GetSourceFileSortName(string sourceFileKey)
    {
        if (string.IsNullOrWhiteSpace(sourceFileKey))
        {
            return "";
        }

        try
        {
            return Path.GetFileName(sourceFileKey);
        }
        catch
        {
            return sourceFileKey;
        }
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
        // 序号始终按真实清单 _rows 顺序，表头临时排序只改显示，不改号（方便同纸张 Shift 多选）。
        for (var i = 0; i < _rows.Count; i++)
        {
            _rows[i].SetNumber(i + 1);
        }
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
        if (TryGetCommonPaperOptions(rows, out var identicalOptions))
        {
            ApplyBatchPaperFromSharedOptions(rows, identicalOptions);
            return;
        }

        if (TryGetCommonAspectRatioPaperOptions(rows, out var aspectOptions))
        {
            ApplyBatchPaperByAspectRatioName(rows, aspectOptions);
            return;
        }

        MessageBox.Show(
            "所选矩形框没有完全相同的候选纸张，且无法用同一套标准/加长图幅（长宽比）统一改纸。",
            Title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>候选列表完全一致时：直接套用同一条纸张检测结果。</summary>

    /// <summary>
    /// 从非模态批打窗弹出子对话框。不要把批打窗设为 Owner：部分 CAD（如中望）会把属主藏掉，
    /// 看起来像「选择纸张时批打界面消失」。顶层模态结束后再把批打窗拉回前台。
    /// </summary>
    private bool? ShowChildModalKeepingListVisible(Window dialog)
    {
        var result = CadDialog.ShowModal(dialog);
        CadWindowFocus.RestoreDialog(this);
        return result;
    }

    private void ApplyBatchPaperFromSharedOptions(IReadOnlyList<Row> rows, IReadOnlyList<PaperDetection> options)
    {
        var dialog = new SinglePlotPaperSelectionForm(options);
        if (ShowChildModalKeepingListVisible(dialog) != true)
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

    /// <summary>
    /// 任意比例场景：用户选图幅名后，按各框自身尺寸重算比例并写入。
    /// </summary>
    private void ApplyBatchPaperByAspectRatioName(IReadOnlyList<Row> rows, IReadOnlyList<PaperDetection> aspectNameOptions)
    {
        var dialog = new SinglePlotPaperSelectionForm(aspectNameOptions);
        if (ShowChildModalKeepingListVisible(dialog) != true)
        {
            return;
        }

        var selectedName = dialog.SelectedPaper.PaperName;
        _suppressPaperEvents = true;
        try
        {
            foreach (var row in rows)
            {
                if (!TryGetJobDrawingSize(row.Job, out var width, out var height))
                {
                    continue;
                }

                var aspectOptions = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(
                    width,
                    height);
                var match = aspectOptions.FirstOrDefault(paper =>
                    string.Equals(paper.PaperName, selectedName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    continue;
                }

                // 下拉改为该框完整长宽比候选，便于后续单行再改。
                row.Options = aspectOptions;
                row.PaperChoice = PaperSizeDetector.FormatOption(match);
                ApplyPaper(row.Job, match);
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

    private static bool CanBatchChangePaper(IReadOnlyList<Row> rows)
        => TryGetCommonPaperOptions(rows, out _)
           || TryGetCommonAspectRatioPaperOptions(rows, out _);

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

    /// <summary>
    /// 各框尺寸不同导致候选比例不一致时，若都能按长宽比解释为同一批标准/加长图幅名，则允许按图幅名批量改纸。
    /// </summary>
    private static bool TryGetCommonAspectRatioPaperOptions(
        IReadOnlyList<Row> rows,
        out IReadOnlyList<PaperDetection> options)
    {
        options = new PaperDetection[0];
        if (rows.Count == 0)
        {
            return false;
        }

        HashSet<string>? commonNames = null;
        IReadOnlyList<PaperDetection>? firstAspect = null;
        foreach (var row in rows)
        {
            if (!TryGetJobDrawingSize(row.Job, out var width, out var height))
            {
                return false;
            }

            var aspect = PaperSizeDetector.DetectRectangleBatchAspectRatioCandidates(
                width,
                height);
            if (aspect.Count == 0)
            {
                return false;
            }

            firstAspect ??= aspect;
            var names = new HashSet<string>(
                aspect.Select(paper => paper.PaperName),
                StringComparer.OrdinalIgnoreCase);
            commonNames = commonNames == null
                ? names
                : new HashSet<string>(commonNames.Where(names.Contains), StringComparer.OrdinalIgnoreCase);
            if (commonNames.Count == 0)
            {
                return false;
            }
        }

        if (firstAspect == null || commonNames == null || commonNames.Count == 0)
        {
            return false;
        }

        // 展示用条目取自首框；真正应用时按各框尺寸重算比例。
        options = firstAspect
            .Where(paper => commonNames.Contains(paper.PaperName))
            .GroupBy(paper => paper.PaperName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return options.Count > 0;
    }

    /// <summary>读取作业对应的图面宽高（用于按长宽比重算纸张/比例）。</summary>
    private static bool TryGetJobDrawingSize(PlotJob job, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (job.UsesUserCoordinateSystem)
        {
            width = Math.Abs(job.UcsMaxX - job.UcsMinX);
            height = Math.Abs(job.UcsMaxY - job.UcsMinY);
            if (width > 1e-9d && height > 1e-9d)
            {
                return true;
            }
        }

        if (job.CornerPoints is { Length: >= 8 } points)
        {
            width = Math.Sqrt(
                (points[2] - points[0]) * (points[2] - points[0])
                + (points[3] - points[1]) * (points[3] - points[1]));
            height = Math.Sqrt(
                (points[4] - points[2]) * (points[4] - points[2])
                + (points[5] - points[3]) * (points[5] - points[3]));
            if (width > 1e-9d && height > 1e-9d)
            {
                return true;
            }
        }

        width = Math.Abs(job.MaxX - job.MinX);
        height = Math.Abs(job.MaxY - job.MinY);
        return width > 1e-9d && height > 1e-9d;
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
        var mergeStem = FileNameSanitizer.Clean(SourceStem());
        if (string.IsNullOrWhiteSpace(mergeStem))
        {
            mergeStem = "合并图纸";
        }
        else
        {
            // 与单页 PDF 区分：合并件在源图名后加「_合并」。
            mergeStem += "_合并";
        }

        var mergedOutput = Path.Combine(directory, mergeStem + ".pdf");
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
                // 先并入出图管道暂存的诊断行（如实际打印样式/线宽开关），保持时间顺序。
                printLogLines.AddRange(BatchPlotLogger.DrainPending());
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
            $"开始通用型批量打印；共={selected.Count}；格式={SelectedOutputFormat}；设备={device}；打印样式={SelectedStyle()}");
        var wasTopmost = Topmost;
        BatchPlotHostProgress.Begin();
        BatchPrintProgressSession? progress = null;
        var progressUiClosed = false;

        // 弹结束提示框前先关进度窗并恢复置顶：进度窗是 Topmost，若仍开着，
        // 提示框可能被压在下面看不见，界面就像一直卡在“正在合并 PDF…”。finally 仍会兜底调用。
        void CloseProgressUi()
        {
            if (progressUiClosed)
            {
                return;
            }

            progressUiClosed = true;
            Topmost = wasTopmost;
            progress?.Dispose();
            progress = null;
            BatchPlotHostProgress.End();
        }

        try
        {
            // 切换按钮为"停止"状态
            _printCts = new CancellationTokenSource();
            _printButton.Content = "停止";
            _printButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(200, 40, 40));
            _printButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(160, 30, 30));
            // 打印期间置顶并保持可见，方便点「停止」；同时抑制 CAD 引擎进度框盖窗。
            Visibility = System.Windows.Visibility.Visible;
            Topmost = true;
            Activate();
            progress = BatchPrintProgressSession.Start(
                this,
                selected.Count,
                () => _printCts?.Cancel());

            if (mergePdf && !_settings.KeepIndividualPdfsWhenMerging)
            {
                temporaryDirectory = Path.Combine(Path.GetTempPath(), "ZwcadBatchPlot", "RectangleMerge_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                for (var i = 0; i < selected.Count; i++)
                {
                    selected[i].OutputPath = Path.Combine(temporaryDirectory, $"{i + 1:D5}.pdf");
                }
            }

            _status.Text = $"打印中... 0 / {selected.Count}";
            progress.Report(0, selected.Count, "准备自定义纸张与输出路径…");
            Pump();

            // 汇总本批所有任意加长尺寸后只更新一次实际 PMP，再进入连续打印。
            CustomPaperBatchPreparer.Prepare(selected, device);
            var results = PdfRasterExport.PlotMany(
                selected, device, SelectedStyle(), _document, _settings,
                beforeJob: job =>
                {
                    completed++;
                    _status.Text = $"打印中... {completed} / {selected.Count}";
                    var finalOutput = originalPaths.TryGetValue(job, out var path) ? path : job.OutputPath;
                    var detail =
                        $"第 {completed} / {selected.Count} 张\n" +
                        $"布局：{job.SpaceName}\n" +
                        $"输出：{System.IO.Path.GetFileName(finalOutput)}";
                    progress?.Report(completed, selected.Count, detail);
                    AppendPrintLog(
                        "INFO",
                        $"开始打印 {completed}/{selected.Count}；源文件={job.SourceFile}；布局={job.SpaceName}；输出={finalOutput}");
                    Visibility = System.Windows.Visibility.Visible;
                    Activate();
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
                progress?.SetPhase("正在合并 PDF…", "请稍候，合并完成后会自动关闭进度窗口。");
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

            _status.Text = $"完成，共 {selected.Count} 张";
            AppendPrintLog("INFO", $"通用型批量打印完成；成功={selected.Count}；失败=0");
            var printLogPath = SavePrintLog();
            var printLogText = BuildLogText(printLogPath);
            CloseProgressUi();
            MessageBox.Show(
                this,
                mergePdf
                    ? $"打印并合并完成，共 {selected.Count} 张，生成 {mergedOutputPaths.Count} 个 PDF。\n{string.Join("\n", mergedOutputPaths)}{printLogText}"
                    : $"打印完成，共 {selected.Count} 张。\n{directory}{printLogText}",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // 与图框批打一致：先弹完成提示，再打开合并 PDF / 输出目录，避免外部程序抢前台把提示框压到下面。
            if (mergePdf && _settings.OpenMergedPdfAfterMerge)
            {
                OpenMergedPdfFiles(mergedOutputPaths);
            }
            else if (!mergePdf && _settings.OpenOutputDirectoryAfterBatchPrint)
            {
                RevealOutput(null, directory);
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"已停止（已完成 {completed} / {selected.Count}）";
            AppendPrintLog("INFO", $"用户取消打印；已开始={completed}/{selected.Count}");
            var printLogPath = SavePrintLog();
            CloseProgressUi();
            MessageBox.Show(this, $"打印已停止。\n已完成 {completed} / {selected.Count} 张。{BuildLogText(printLogPath)}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "打印失败";
            AppendPrintLog("ERROR", "通用型批量打印失败: " + ex);
            var printLogPath = SavePrintLog();
            CloseProgressUi();
            MessageBox.Show(this, "通用型批量打印失败: " + ex.Message + BuildLogText(printLogPath), Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _printCts?.Dispose();
            _printCts = null;
            // 恢复按钮
            _printButton.Content = "开始打印";
            _printButton.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215));
            _printButton.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 95, 170));
            CloseProgressUi();

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
            "通用型批量拆图",
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
                if (_settings.OpenOutputDirectoryAfterBatchPrint)
            {
                RevealOutput(null, directory);
            }
            _status.Text = $"拆图完成，共 {selected.Count} 张";
            MessageBox.Show($"DWG 拆图完成，共 {selected.Count} 张。\n{directory}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "拆图失败";
            MessageBox.Show("通用型批量拆图失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
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
        // PNG/JPG 先出 PDF 再转图，留白与 PDF 相同，按当前勾选写入作业。
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
        if (_suppressSequenceOverlayFromMultiFile)
        {
            ClearSequenceOverlay();
            return;
        }

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
    private bool IsDwfOutput => string.Equals(SelectedOutputFormat, "DWF", StringComparison.OrdinalIgnoreCase);
    /// <summary>DWG 拆图不走留白；PNG/JPG 先出 PDF 再转图，留白语义与 PDF 相同。</summary>
    private bool SupportsLeaveMargin => !IsDwgOutput;
    private string? AutomaticOutputSubfolder => _savePathModeCombo.SelectedIndex == 1
        ? FileNameSanitizer.Clean(SelectedOutputFormat)
        : null;
    /// <summary>PNG/JPG 与 PDF 共用 PDF 绘图仪，打印后再按设置 DPI 转图。</summary>
    private string SelectedDevice() => IsDwfOutput
        ? _dwfPlotDevice
        : AcadPlotterInstaller.PreferredPdfPlotter;
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
        if (ShowChildModalKeepingListVisible(dialog) != true) return;
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
                != updated.RecognizeFourLineRectangleFrames
                || !ScalesEqual(_settings.CustomScales, updated.CustomScales);
            _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
            _settings.RecognizeFourLineRectangleFrames = updated.RecognizeFourLineRectangleFrames;
            _settings.CustomScales = updated.CustomScales;
            _settings.HideFrameBoundaryWhenPlotting = updated.HideFrameBoundaryWhenPlotting;
            _settings.PlotTransparency = updated.PlotTransparency;
            _settings.PlotObjectLineweights = updated.PlotObjectLineweights;
            _settings.GeneratePrintLog = updated.GeneratePrintLog;
            _settings.ConvertTextToGeometryWhenPlotting = updated.ConvertTextToGeometryWhenPlotting;
            _settings.LongPaperSnapToleranceMm = updated.LongPaperSnapToleranceMm;
            _settings.LongPaperNameFormat = updated.LongPaperNameFormat;
            _settings.SortOrderHorizontalFirst = updated.SortOrderHorizontalFirst;
            _settings.PdfFileNamePattern = updated.PdfFileNamePattern;
            _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
            _settings.FileNameSequenceDigits = updated.FileNameSequenceDigits;
            _settings.AutoFileNameSequenceDigits = updated.AutoFileNameSequenceDigits;
            _settings.FileNameSequenceStartNumber = updated.FileNameSequenceStartNumber;

            if (!form.RequestPickDirectoryRowHeight
                && !form.RequestPickDirectoryTextAppearance
                && !form.RequestPickScaleFromCad
                && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                if (recognitionSettingsChanged)
                {
                    // 开关或纸张容差变化后立即沿用上次扫描范围重扫，避免列表仍显示旧识别结果。
                    ReloadFrames();
                }
                else
                {
                    // 文件名规则或序号设置变更后刷新列表输出名。
                    RefreshFileNames();
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
                if (form.RequestPickScaleFromCad)
                {
                    // 与目录「图中交互」同一套路：设置窗已关，回到 CAD 框选后再重开比例页。
                    // 只写入自定义比例，不自动重扫；需要新比例生效时由用户自行扫描。
                    ok = ScaleSettingsPicker.PromptScaleFromFrame(document, settings, out settings, out message);
                    _settings.CustomScales = settings.CustomScales;
                }
                else if (form.RequestPickDirectoryTextAppearance)
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

                // 成功后设置页会重开并显示新值，不必再弹“已设置”；失败/取消仍提示。
                if (!ok)
                {
                    MessageBox.Show(message, "批量打印设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
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

    /// <summary>比较自定义比例列表是否一致，用于判断是否需要重扫。</summary>
    private static bool ScalesEqual(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (Math.Abs(left[i] - right[i]) > 1e-9)
            {
                return false;
            }
        }

        return true;
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
