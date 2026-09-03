using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
#if AUTOCAD
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.DatabaseServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed partial class TitleBlockLibraryManagerForm : Window
{
    private readonly BindingList<TitleBlockRow> _rows = new();
    private readonly BindingList<TitleBlockRow> _displayRows = new();
    private bool _loading;
    private bool _dirty;
    private bool _showingValidationError;
    private int _sortColumnIndex = -1;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;
    private HashSet<string> _presentBlockNames = new(StringComparer.OrdinalIgnoreCase);

    public bool LibraryChanged { get; private set; }

    public TitleBlockLibraryManagerForm()
    {
        InitializeComponent();

        AddColumns();
        _grid.ItemsSource = _displayRows;

        // 单元格编辑提交即标记未保存（绑定 UpdateSourceTrigger=PropertyChanged，等价原 CurrentCellDirtyStateChanged + CellValueChanged）。
        _displayRows.ListChanged += (_, e) =>
        {
            if (e.ListChangedType == ListChangedType.ItemChanged)
            {
                MarkDirty();
            }
        };

        // 双击行编辑（XAML MouseDoubleClick）。
        // 右键行选择 + 右键菜单（XAML ContextMenu / PreviewMouseRightButtonDown）。

        Closing += OnFormClosing;
        Activated += (_, _) => RefreshPresentBlocks();

        LoadRows();
    }

    private void AddColumns()
    {
        _grid.Columns.Clear();

        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.BlockName), "块名", 190, numeric: false));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.CreatedAt), "加入时间", 170, numeric: false, readOnly: true));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PaperName), "图幅", 80, numeric: false));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PaperWidthMm), "纸宽mm", 90));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PaperHeightMm), "纸高mm", 90));
        _grid.Columns.Add(MakeCheckColumn(nameof(TitleBlockRow.HasPrintRegion), "有打印边界", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PrintMinX), "边界MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PrintMinY), "边界MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PrintMaxX), "边界MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PrintMaxY), "边界MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.TitleMinX), "图名MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.TitleMinY), "图名MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.TitleMaxX), "图名MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.TitleMaxY), "图名MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.NumberMinX), "图号MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.NumberMinY), "图号MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.NumberMaxX), "图号MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.NumberMaxY), "图号MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.DateMinX), "日期MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.DateMinY), "日期MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.DateMaxX), "日期MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.DateMaxY), "日期MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.RevisionMinX), "版次MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.RevisionMinY), "版次MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.RevisionMaxX), "版次MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.RevisionMaxY), "版次MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PhaseMinX), "设计阶段MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PhaseMinY), "设计阶段MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PhaseMaxX), "设计阶段MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.PhaseMaxY), "设计阶段MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info1MinX), "信息1MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info1MinY), "信息1MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info1MaxX), "信息1MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info1MaxY), "信息1MaxY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info2MinX), "信息2MinX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info2MinY), "信息2MinY", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info2MaxX), "信息2MaxX", 96));
        _grid.Columns.Add(MakeTextColumn(nameof(TitleBlockRow.Info2MaxY), "信息2MaxY", 96));

        // 最后一列对应原 AutoSizeMode = Fill。
        var updatedAt = new DataGridTextColumn
        {
            Header = "更新时间",
            Binding = new Binding(nameof(TitleBlockRow.UpdatedAt)) { Mode = BindingMode.OneWay },
            IsReadOnly = true,
            MinWidth = 170,
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
        };
        _grid.Columns.Add(updatedAt);

        foreach (var column in _grid.Columns)
        {
            // 原 SortMode = Programmatic：排序由表头点击事件手工处理。
            column.CanUserSort = true;
            column.SortMemberPath = ((Binding)((DataGridBoundColumn)column).Binding).Path.Path;
        }
    }

    private static DataGridTextColumn MakeTextColumn(string propertyName, string header, double width, bool numeric = true, bool readOnly = false)
    {
        var binding = new Binding(propertyName)
        {
            Mode = readOnly ? BindingMode.OneWay : BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        };
        if (!readOnly && numeric)
        {
            binding.ValidationRules.Add(new DoubleValidationRule());
        }

        return new DataGridTextColumn
        {
            Header = header,
            Binding = binding,
            IsReadOnly = readOnly,
            Width = width
        };
    }

    private static DataGridCheckBoxColumn MakeCheckColumn(string propertyName, string header, double width)
    {
        return new DataGridCheckBoxColumn
        {
            Header = header,
            Binding = new Binding(propertyName)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            },
            Width = width
        };
    }

    private void LoadRows()
    {
        if (_dirty
            && System.Windows.MessageBox.Show("当前有未保存的修改，确定重新读取并放弃这些修改吗？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        ExecuteSafely("重新读取图框库", () =>
        {
            _loading = true;
            try
            {
                _rows.Clear();
                foreach (var block in TitleBlockLibraryStore.Load().Blocks.OrderBy(x => x.BlockName, StringComparer.CurrentCultureIgnoreCase))
                {
                    _rows.Add(TitleBlockRow.FromDefinition(block));
                }

                RefreshDisplayRows();
                _dirty = false;
                RefreshPresentBlocks();
                RefreshStatus();
            }
            finally
            {
                _loading = false;
            }
        });
    }

    private void SaveRows()
    {
        if (!TryBuildLibrary(out var library))
        {
            return;
        }

        ExecuteSafely("保存图框信息库", () =>
        {
            TitleBlockLibraryStore.Save(library);
            LibraryChanged = true;
            _dirty = false;
            RefreshStatus();
            System.Windows.MessageBox.Show("图框信息库已保存。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedItems.Count == 0)
        {
            return;
        }

        if (System.Windows.MessageBox.Show("确定删除选中的图框定义吗？", "图框信息库管理", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        var selected = _grid.SelectedItems
            .OfType<TitleBlockRow>()
            .ToList();

        foreach (var row in selected)
        {
            _rows.Remove(row);
        }

        RefreshDisplayRows();
        _dirty = true;
        RefreshStatus();
        SaveRows();
    }

    private void GridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(source);
        if (row == null)
        {
            _grid.SelectedItems.Clear();
            return;
        }

        // 右键所在行必须先成为当前选择，否则菜单“编辑”可能作用到此前选中的另一条记录。
        if (!_grid.SelectedItems.Contains(row.Item))
        {
            _grid.SelectedItems.Clear();
            _grid.SelectedItems.Add(row.Item);
        }

        var cell = FindAncestor<DataGridCell>(source);
        if (cell != null)
        {
            _grid.CurrentCell = new DataGridCellInfo(cell);
        }
    }

    private void GridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<DataGridRow>(source) != null)
        {
            EditSelectedDefinition();
        }
    }

    private void GridContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 对应原 _rowMenu.Opening：无选中行时取消菜单。
        if (_grid.SelectedItems.Count == 0)
        {
            e.Handled = true;
        }
    }

    private void OnEditMenuItemClick(object sender, RoutedEventArgs e)
    {
        EditSelectedDefinition();
    }

    private static T? FindAncestor<T>(DependencyObject source)
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

    private void EditSelectedDefinition()
    {
        CommitEdit();

        var row = _grid.SelectedItems.OfType<TitleBlockRow>().FirstOrDefault();
        if (row == null)
        {
            return;
        }

        // 表格内若已有手工修改，先明确保存，避免编辑窗口从磁盘回读到另一份旧配置。
        if (_dirty)
        {
            var saveFirst = System.Windows.MessageBox.Show(
                "当前有未保存的表格修改。编辑图框前需要先保存，是否继续？",
                Title,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (saveFirst != MessageBoxResult.OK)
            {
                return;
            }

            SaveRows();
            if (_dirty)
            {
                return;
            }
        }

        CadWindowFocus.HideForCadInput(this);
        try
        {
            if (BatchPlotCommands.EditTitleBlockFromLibrary(row.BlockName))
            {
                LibraryChanged = true;
                LoadRows();
            }
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private void RefreshPresentBlocks()
    {
        _presentBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            using var tr = doc.Database.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId ownerId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                foreach (ObjectId id in owner)
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                    {
                        continue;
                    }

                    var blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                    _presentBlockNames.Add(blockName);
                    _presentBlockNames.Add(CadTextExtractor.GetLibraryIdentityName(blockRef, tr));

                    if (CadTextExtractor.TryGetVisibleNestedBlockName(tr, blockRef, out var innerName))
                    {
                        _presentBlockNames.Add(blockName + "+" + innerName);
                    }
                }
            }

            tr.Commit();
        }
        catch
        {
            // 无激活文档或图纸不可读时不报错，列表行仅不回显粉色。
        }

        // 通过行对象属性触发 RowStyle DataTrigger，等价原 RowPrePaint 淡粉色整行回显。
        foreach (var row in _displayRows)
        {
            row.PresentInDrawing = !string.IsNullOrWhiteSpace(row.BlockName) && _presentBlockNames.Contains(row.BlockName);
        }
    }

    private void GridLoadingRow(object sender, DataGridRowEventArgs e)
    {
        // 虚拟化滚动时新容器也要立即回显已存在的图框行。
        if (e.Row.Item is TitleBlockRow row)
        {
            row.PresentInDrawing = !string.IsNullOrWhiteSpace(row.BlockName) && _presentBlockNames.Contains(row.BlockName);
        }
    }

    private void GridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var columnIndex = _grid.Columns.IndexOf(e.Column);
        if (columnIndex < 0 || columnIndex >= _grid.Columns.Count)
        {
            return;
        }

        // 同一列表头再次点击时切换升序/降序；切换到其他列时从升序开始。
        if (_sortColumnIndex == columnIndex)
        {
            _sortDirection = _sortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _sortColumnIndex = columnIndex;
            _sortDirection = ListSortDirection.Ascending;
        }

        CommitEdit();
        RefreshDisplayRows();
    }

    private void RefreshDisplayRows()
    {
        IEnumerable<TitleBlockRow> rows = _rows;
        if (_sortColumnIndex >= 0 && _sortColumnIndex < _grid.Columns.Count)
        {
            var propertyName = _grid.Columns[_sortColumnIndex].SortMemberPath;
            var property = TypeDescriptor.GetProperties(typeof(TitleBlockRow))[propertyName];
            if (property != null)
            {
                var comparer = Comparer<TitleBlockRow>.Create((left, right) =>
                    CompareSortValues(property.GetValue(left), property.GetValue(right)));
                rows = _sortDirection == ListSortDirection.Ascending
                    ? rows.OrderBy(row => row, comparer)
                    : rows.OrderByDescending(row => row, comparer);
            }
        }

        var wasLoading = _loading;
        _loading = true;
        try
        {
            // 排序属于视图操作，不能误标记为未保存修改（_loading 守卫屏蔽 ListChanged）。
            ReplaceBindingListContents(_displayRows, rows);
            UpdateSortGlyph();
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static int CompareSortValues(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return -1;
        }

        if (right == null)
        {
            return 1;
        }

        if (left is string leftText && right is string rightText)
        {
            return NaturalStringComparer.Instance.Compare(leftText, rightText);
        }

        return left is IComparable comparable
            ? comparable.CompareTo(right)
            : string.Compare(left.ToString(), right.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateSortGlyph()
    {
        foreach (var column in _grid.Columns)
        {
            column.SortDirection = null;
        }

        if (_sortColumnIndex >= 0 && _sortColumnIndex < _grid.Columns.Count)
        {
            _grid.Columns[_sortColumnIndex].SortDirection = _sortDirection;
        }
    }

    /// <summary>
    /// 排序只替换界面绑定列表，不改变图框库的原始集合顺序；编辑、删除仍操作同一行对象。
    /// </summary>
    private static void ReplaceBindingListContents<T>(BindingList<T> target, IEnumerable<T> values)
    {
        var snapshot = values.ToList();
        target.RaiseListChangedEvents = false;
        try
        {
            target.Clear();
            foreach (var value in snapshot)
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

    private void ImportLibrary()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            Title = "导入图框库"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (System.Windows.MessageBox.Show("导入会覆盖当前图框信息库，确定继续吗？", Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        ExecuteSafely("导入图框库", () =>
        {
            var library = TitleBlockLibraryStore.Load(dialog.FileName);
            TitleBlockLibraryStore.Save(library);
            LibraryChanged = true;
            _dirty = false;
            LoadRows();
            System.Windows.MessageBox.Show("图框库已导入。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void ExportLibrary()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            FileName = "TitleBlockLibrary.json",
            Title = "导出图框库"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!TryBuildLibrary(out var library))
        {
            return;
        }

        ExecuteSafely("导出图框库", () =>
        {
            TitleBlockLibraryStore.Save(library, dialog.FileName);
            System.Windows.MessageBox.Show("图框库已导出。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    private void RefreshStatus()
    {
        _status.Text = $"共 {_rows.Count} 个图框定义。双击行或右键选择“编辑”。{(_dirty ? "有未保存修改。" : "")} 配置文件: {TitleBlockLibraryStore.DefaultPath}";
    }

    private bool TryBuildLibrary(out TitleBlockLibrary library)
    {
        library = new TitleBlockLibrary();
        CommitEdit();

        var invalid = _rows.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.BlockName));
        if (invalid != null)
        {
            System.Windows.MessageBox.Show("块名不能为空。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var invalidNumber = _rows.FirstOrDefault(x => !x.HasFiniteNumbers());
        if (invalidNumber != null)
        {
            System.Windows.MessageBox.Show($"图框 {invalidNumber.BlockName} 含有无效数字。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var invalidPaper = _rows.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.PaperName)
            && (x.PaperWidthMm <= 0 || x.PaperHeightMm <= 0));
        if (invalidPaper != null)
        {
            System.Windows.MessageBox.Show($"图框 {invalidPaper.BlockName} 设置了图幅，但纸宽/纸高必须大于 0。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var invalidRegion = _rows.FirstOrDefault(x =>
            x.HasPrintRegion
            && (Math.Abs(x.PrintMaxX - x.PrintMinX) <= 1e-6 || Math.Abs(x.PrintMaxY - x.PrintMinY) <= 1e-6));
        if (invalidRegion != null)
        {
            System.Windows.MessageBox.Show($"图框 {invalidRegion.BlockName} 的打印边界宽度和高度必须大于 0。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var duplicated = _rows
            .GroupBy(x => x.BlockName.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated != null)
        {
            System.Windows.MessageBox.Show("存在重复块名: " + duplicated.Key, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        foreach (var row in _rows)
        {
            library.Blocks.Add(row.ToDefinition());
        }

        return true;
    }

    private void MarkDirty()
    {
        if (_loading)
        {
            return;
        }

        _dirty = true;
        RefreshStatus();
    }

    private void CommitEdit()
    {
        // 等价原 _grid.EndEdit()。
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void GridValidationError(object sender, ValidationErrorEventArgs e)
    {
        if (_showingValidationError)
        {
            return;
        }

        _showingValidationError = true;
        try
        {
            // 等价原 DataError 提示。
            System.Windows.MessageBox.Show(this, "输入值格式不正确，请输入有效的数字。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _showingValidationError = false;
        }
    }

    private void OnFormClosing(object? sender, CancelEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "当前有未保存的修改。是否先保存？",
            Title,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (result == MessageBoxResult.Yes)
        {
            SaveRows();
            e.Cancel = _dirty;
        }
    }

    private void ExecuteSafely(string action, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(action + "失败: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── 顶部按钮 Click 处理 ──

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveRows();

    private void OnDeleteClick(object sender, RoutedEventArgs e) => DeleteSelected();

    private void OnReloadClick(object sender, RoutedEventArgs e) => LoadRows();

    private void OnImportClick(object sender, RoutedEventArgs e) => ImportLibrary();

    private void OnExportClick(object sender, RoutedEventArgs e) => ExportLibrary();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        ExecuteSafely("打开配置目录", () =>
        {
            Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = TitleBlockLibraryStore.DefaultDirectory,
                UseShellExecute = true
            });
        });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>数值列输入校验（等价原 DataGridView DataError 提示）。</summary>
    private sealed class DoubleValidationRule : ValidationRule
    {
        public override ValidationResult Validate(object value, CultureInfo cultureInfo)
        {
            var text = value as string;
            if (string.IsNullOrEmpty(text))
            {
                // 空值交给业务校验（HasFiniteNumbers / 块名为空等）处理。
                return ValidationResult.ValidResult;
            }

            return double.TryParse(text, NumberStyles.Float, cultureInfo, out _)
                ? ValidationResult.ValidResult
                : new ValidationResult(false, "输入值格式不正确，请输入有效的数字。");
        }
    }

    private sealed class TitleBlockRow : INotifyPropertyChanged
    {
        private string _blockName = "";
        private string _paperName = "";
        private double _paperWidthMm;
        private double _paperHeightMm;
        private bool _hasPrintRegion;
        private string _coordinateMode = "Local";
        private double _printMinX;
        private double _printMinY;
        private double _printMaxX;
        private double _printMaxY;
        private double _titleMinX;
        private double _titleMinY;
        private double _titleMaxX;
        private double _titleMaxY;
        private double _numberMinX;
        private double _numberMinY;
        private double _numberMaxX;
        private double _numberMaxY;
        private double _dateMinX;
        private double _dateMinY;
        private double _dateMaxX;
        private double _dateMaxY;
        private double _revisionMinX;
        private double _revisionMinY;
        private double _revisionMaxX;
        private double _revisionMaxY;
        private double _phaseMinX;
        private double _phaseMinY;
        private double _phaseMaxX;
        private double _phaseMaxY;
        private double _info1MinX;
        private double _info1MinY;
        private double _info1MaxX;
        private double _info1MaxY;
        private double _info2MinX;
        private double _info2MinY;
        private double _info2MaxX;
        private double _info2MaxY;
        private DateTime _createdAt;
        private DateTime _updatedAt;
        private bool _presentInDrawing;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string BlockName { get => _blockName; set => Set(ref _blockName, value); }
        public string PaperName { get => _paperName; set => Set(ref _paperName, value); }
        public double PaperWidthMm { get => _paperWidthMm; set => Set(ref _paperWidthMm, value); }
        public double PaperHeightMm { get => _paperHeightMm; set => Set(ref _paperHeightMm, value); }
        public bool HasPrintRegion { get => _hasPrintRegion; set => Set(ref _hasPrintRegion, value); }
        public string CoordinateMode { get => _coordinateMode; set => Set(ref _coordinateMode, value); }
        public double PrintMinX { get => _printMinX; set => Set(ref _printMinX, value); }
        public double PrintMinY { get => _printMinY; set => Set(ref _printMinY, value); }
        public double PrintMaxX { get => _printMaxX; set => Set(ref _printMaxX, value); }
        public double PrintMaxY { get => _printMaxY; set => Set(ref _printMaxY, value); }
        public double TitleMinX { get => _titleMinX; set => Set(ref _titleMinX, value); }
        public double TitleMinY { get => _titleMinY; set => Set(ref _titleMinY, value); }
        public double TitleMaxX { get => _titleMaxX; set => Set(ref _titleMaxX, value); }
        public double TitleMaxY { get => _titleMaxY; set => Set(ref _titleMaxY, value); }
        public double NumberMinX { get => _numberMinX; set => Set(ref _numberMinX, value); }
        public double NumberMinY { get => _numberMinY; set => Set(ref _numberMinY, value); }
        public double NumberMaxX { get => _numberMaxX; set => Set(ref _numberMaxX, value); }
        public double NumberMaxY { get => _numberMaxY; set => Set(ref _numberMaxY, value); }
        public double DateMinX { get => _dateMinX; set => Set(ref _dateMinX, value); }
        public double DateMinY { get => _dateMinY; set => Set(ref _dateMinY, value); }
        public double DateMaxX { get => _dateMaxX; set => Set(ref _dateMaxX, value); }
        public double DateMaxY { get => _dateMaxY; set => Set(ref _dateMaxY, value); }
        public double RevisionMinX { get => _revisionMinX; set => Set(ref _revisionMinX, value); }
        public double RevisionMinY { get => _revisionMinY; set => Set(ref _revisionMinY, value); }
        public double RevisionMaxX { get => _revisionMaxX; set => Set(ref _revisionMaxX, value); }
        public double RevisionMaxY { get => _revisionMaxY; set => Set(ref _revisionMaxY, value); }
        public double PhaseMinX { get => _phaseMinX; set => Set(ref _phaseMinX, value); }
        public double PhaseMinY { get => _phaseMinY; set => Set(ref _phaseMinY, value); }
        public double PhaseMaxX { get => _phaseMaxX; set => Set(ref _phaseMaxX, value); }
        public double PhaseMaxY { get => _phaseMaxY; set => Set(ref _phaseMaxY, value); }
        public double Info1MinX { get => _info1MinX; set => Set(ref _info1MinX, value); }
        public double Info1MinY { get => _info1MinY; set => Set(ref _info1MinY, value); }
        public double Info1MaxX { get => _info1MaxX; set => Set(ref _info1MaxX, value); }
        public double Info1MaxY { get => _info1MaxY; set => Set(ref _info1MaxY, value); }
        public double Info2MinX { get => _info2MinX; set => Set(ref _info2MinX, value); }
        public double Info2MinY { get => _info2MinY; set => Set(ref _info2MinY, value); }
        public double Info2MaxX { get => _info2MaxX; set => Set(ref _info2MaxX, value); }
        public double Info2MaxY { get => _info2MaxY; set => Set(ref _info2MaxY, value); }
        public DateTime CreatedAt { get => _createdAt; set => Set(ref _createdAt, value); }
        public DateTime UpdatedAt { get => _updatedAt; set => Set(ref _updatedAt, value); }

        /// <summary>当前 CAD 图纸中已存在该图框时整行淡粉色回显（视图辅助，不参与序列化）。</summary>
        public bool PresentInDrawing { get => _presentInDrawing; set => Set(ref _presentInDrawing, value); }

        private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public static TitleBlockRow FromDefinition(TitleBlockDefinition definition)
        {
            return new TitleBlockRow
            {
                BlockName = definition.BlockName,
                PaperName = definition.PaperName,
                PaperWidthMm = definition.PaperWidthMm,
                PaperHeightMm = definition.PaperHeightMm,
                HasPrintRegion = definition.HasPrintRegion,
                CoordinateMode = string.IsNullOrWhiteSpace(definition.CoordinateMode) ? "Local" : definition.CoordinateMode,
                PrintMinX = definition.PrintRegion.MinX,
                PrintMinY = definition.PrintRegion.MinY,
                PrintMaxX = definition.PrintRegion.MaxX,
                PrintMaxY = definition.PrintRegion.MaxY,
                TitleMinX = definition.TitleRegion.MinX,
                TitleMinY = definition.TitleRegion.MinY,
                TitleMaxX = definition.TitleRegion.MaxX,
                TitleMaxY = definition.TitleRegion.MaxY,
                NumberMinX = definition.DrawingNumberRegion.MinX,
                NumberMinY = definition.DrawingNumberRegion.MinY,
                NumberMaxX = definition.DrawingNumberRegion.MaxX,
                NumberMaxY = definition.DrawingNumberRegion.MaxY,
                DateMinX = definition.DateRegion.MinX,
                DateMinY = definition.DateRegion.MinY,
                DateMaxX = definition.DateRegion.MaxX,
                DateMaxY = definition.DateRegion.MaxY,
                RevisionMinX = definition.RevisionRegion.MinX,
                RevisionMinY = definition.RevisionRegion.MinY,
                RevisionMaxX = definition.RevisionRegion.MaxX,
                RevisionMaxY = definition.RevisionRegion.MaxY,
                PhaseMinX = definition.PhaseRegion.MinX,
                PhaseMinY = definition.PhaseRegion.MinY,
                PhaseMaxX = definition.PhaseRegion.MaxX,
                PhaseMaxY = definition.PhaseRegion.MaxY,
                Info1MinX = definition.Info1Region.MinX,
                Info1MinY = definition.Info1Region.MinY,
                Info1MaxX = definition.Info1Region.MaxX,
                Info1MaxY = definition.Info1Region.MaxY,
                Info2MinX = definition.Info2Region.MinX,
                Info2MinY = definition.Info2Region.MinY,
                Info2MaxX = definition.Info2Region.MaxX,
                Info2MaxY = definition.Info2Region.MaxY,
                CreatedAt = definition.CreatedAt,
                UpdatedAt = definition.UpdatedAt
            };
        }

        public TitleBlockDefinition ToDefinition()
        {
            return new TitleBlockDefinition
            {
                BlockName = BlockName.Trim(),
                PaperName = PaperName?.Trim() ?? "",
                PaperWidthMm = PaperWidthMm,
                PaperHeightMm = PaperHeightMm,
                HasPrintRegion = HasPrintRegion,
                CoordinateMode = string.IsNullOrWhiteSpace(CoordinateMode) ? "Local" : CoordinateMode.Trim(),
                PrintRegion = LocalRectangle.FromPoints(PrintMinX, PrintMinY, PrintMaxX, PrintMaxY),
                TitleRegion = LocalRectangle.FromPoints(TitleMinX, TitleMinY, TitleMaxX, TitleMaxY),
                DrawingNumberRegion = LocalRectangle.FromPoints(NumberMinX, NumberMinY, NumberMaxX, NumberMaxY),
                DateRegion = LocalRectangle.FromPoints(DateMinX, DateMinY, DateMaxX, DateMaxY),
                RevisionRegion = LocalRectangle.FromPoints(RevisionMinX, RevisionMinY, RevisionMaxX, RevisionMaxY),
                PhaseRegion = LocalRectangle.FromPoints(PhaseMinX, PhaseMinY, PhaseMaxX, PhaseMaxY),
                Info1Region = LocalRectangle.FromPoints(Info1MinX, Info1MinY, Info1MaxX, Info1MaxY),
                Info2Region = LocalRectangle.FromPoints(Info2MinX, Info2MinY, Info2MaxX, Info2MaxY),
                CreatedAt = CreatedAt == default ? DateTime.Now : CreatedAt,
                UpdatedAt = DateTime.Now
            };
        }

        public bool HasFiniteNumbers()
        {
            return IsFinite(PaperWidthMm)
                && IsFinite(PaperHeightMm)
                && IsFinite(PrintMinX)
                && IsFinite(PrintMinY)
                && IsFinite(PrintMaxX)
                && IsFinite(PrintMaxY)
                && IsFinite(TitleMinX)
                && IsFinite(TitleMinY)
                && IsFinite(TitleMaxX)
                && IsFinite(TitleMaxY)
                && IsFinite(NumberMinX)
                && IsFinite(NumberMinY)
                && IsFinite(NumberMaxX)
                && IsFinite(NumberMaxY)
                && IsFinite(DateMinX) && IsFinite(DateMinY) && IsFinite(DateMaxX) && IsFinite(DateMaxY)
                && IsFinite(RevisionMinX) && IsFinite(RevisionMinY) && IsFinite(RevisionMaxX) && IsFinite(RevisionMaxY)
                && IsFinite(PhaseMinX) && IsFinite(PhaseMinY) && IsFinite(PhaseMaxX) && IsFinite(PhaseMaxY)
                && IsFinite(Info1MinX) && IsFinite(Info1MinY) && IsFinite(Info1MaxX) && IsFinite(Info1MaxY)
                && IsFinite(Info2MinX) && IsFinite(Info2MinY) && IsFinite(Info2MaxX) && IsFinite(Info2MaxY);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
