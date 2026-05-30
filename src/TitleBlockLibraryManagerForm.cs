using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public sealed class TitleBlockLibraryManagerForm : Form
{
    private readonly BindingList<TitleBlockRow> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();

    public TitleBlockLibraryManagerForm()
    {
        InitializeComponents();
        LoadRows();
    }

    private void InitializeComponents()
    {
        Text = "图框信息库管理";
        UiLayout.ConfigureForm(this, 1080, 640, 900, 520);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.ButtonHeight() + UiLayout.Scale(18),
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(6)),
            WrapContents = false,
            AutoScroll = true
        };

        Button MakeButton(string text, int width)
        {
            return UiLayout.CreateButton(text, width);
        }

        var saveButton = MakeButton("保存修改", 96);
        saveButton.Click += (_, _) => SaveRows();

        var deleteButton = MakeButton("删除选中", 96);
        deleteButton.Click += (_, _) => DeleteSelected();

        var reloadButton = MakeButton("重新读取", 96);
        reloadButton.Click += (_, _) => LoadRows();

        var importButton = MakeButton("导入图框库", 104);
        importButton.Click += (_, _) => ImportLibrary();

        var exportButton = MakeButton("导出图框库", 104);
        exportButton.Click += (_, _) => ExportLibrary();

        var openFolderButton = MakeButton("打开配置目录", 112);
        openFolderButton.Click += (_, _) =>
        {
            Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
            System.Diagnostics.Process.Start(TitleBlockLibraryStore.DefaultDirectory);
        };

        var closeButton = MakeButton("关闭", 72);
        closeButton.Click += (_, _) => Close();

        top.Controls.Add(saveButton);
        top.Controls.Add(deleteButton);
        top.Controls.Add(reloadButton);
        top.Controls.Add(importButton);
        top.Controls.Add(exportButton);
        top.Controls.Add(openFolderButton);
        top.Controls.Add(closeButton);

        UiLayout.StyleGrid(_grid, Font);
        _grid.DataSource = _rows;

        AddColumns();

        _status.Dock = DockStyle.Bottom;
        _status.Height = Math.Max(UiLayout.Scale(28), Font.Height + UiLayout.Scale(10));
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);

        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(top);
    }

    private void AddColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.BlockName), HeaderText = "块名", Width = UiLayout.Scale(190) });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(TitleBlockRow.HasPrintRegion), HeaderText = "有打印边界", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMinX), HeaderText = "边界MinX", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMinY), HeaderText = "边界MinY", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMaxX), HeaderText = "边界MaxX", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMaxY), HeaderText = "边界MaxY", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMinX), HeaderText = "图名MinX", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMinY), HeaderText = "图名MinY", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMaxX), HeaderText = "图名MaxX", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMaxY), HeaderText = "图名MaxY", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMinX), HeaderText = "图号MinX", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMinY), HeaderText = "图号MinY", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMaxX), HeaderText = "图号MaxX", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMaxY), HeaderText = "图号MaxY", Width = UiLayout.Scale(96) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.UpdatedAt), HeaderText = "更新时间", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = UiLayout.Scale(170), ReadOnly = true });
    }

    private void LoadRows()
    {
        _rows.Clear();
        foreach (var block in TitleBlockLibraryStore.Load().Blocks.OrderBy(x => x.BlockName, StringComparer.CurrentCultureIgnoreCase))
        {
            _rows.Add(TitleBlockRow.FromDefinition(block));
        }

        RefreshStatus();
    }

    private void SaveRows()
    {
        _grid.EndEdit();
        var invalid = _rows.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.BlockName));
        if (invalid != null)
        {
            MessageBox.Show("块名不能为空。", "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var duplicated = _rows
            .GroupBy(x => x.BlockName.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated != null)
        {
            MessageBox.Show("存在重复块名: " + duplicated.Key, "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var library = new TitleBlockLibrary();
        foreach (var row in _rows)
        {
            library.Blocks.Add(row.ToDefinition());
        }

        TitleBlockLibraryStore.Save(library);
        DialogResult = DialogResult.OK;
        RefreshStatus();
        MessageBox.Show("图框信息库已保存。", "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void DeleteSelected()
    {
        if (_grid.SelectedRows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("确定删除选中的图框定义吗？", "图框信息库管理", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var selected = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<TitleBlockRow>()
            .ToList();

        foreach (var row in selected)
        {
            _rows.Remove(row);
        }

        RefreshStatus();
    }

    private void ImportLibrary()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            Title = "导入图框库"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var library = TitleBlockLibraryStore.Load(dialog.FileName);
        TitleBlockLibraryStore.Save(library);
        LoadRows();
    }

    private void ExportLibrary()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            FileName = "TitleBlockLibrary.json",
            Title = "导出图框库"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        TitleBlockLibraryStore.Save(TitleBlockLibraryStore.Load(), dialog.FileName);
        MessageBox.Show("图框库已导出。", "图框信息库管理", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void RefreshStatus()
    {
        _status.Text = $"共 {_rows.Count} 个图框定义。配置文件: {TitleBlockLibraryStore.DefaultPath}";
    }

    private sealed class TitleBlockRow
    {
        public string BlockName { get; set; } = "";
        public bool HasPrintRegion { get; set; }
        public double PrintMinX { get; set; }
        public double PrintMinY { get; set; }
        public double PrintMaxX { get; set; }
        public double PrintMaxY { get; set; }
        public double TitleMinX { get; set; }
        public double TitleMinY { get; set; }
        public double TitleMaxX { get; set; }
        public double TitleMaxY { get; set; }
        public double NumberMinX { get; set; }
        public double NumberMinY { get; set; }
        public double NumberMaxX { get; set; }
        public double NumberMaxY { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public static TitleBlockRow FromDefinition(TitleBlockDefinition definition)
        {
            return new TitleBlockRow
            {
                BlockName = definition.BlockName,
                HasPrintRegion = definition.HasPrintRegion,
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
                CreatedAt = definition.CreatedAt,
                UpdatedAt = definition.UpdatedAt
            };
        }

        public TitleBlockDefinition ToDefinition()
        {
            return new TitleBlockDefinition
            {
                BlockName = BlockName.Trim(),
                HasPrintRegion = HasPrintRegion,
                PrintRegion = LocalRectangle.FromPoints(PrintMinX, PrintMinY, PrintMaxX, PrintMaxY),
                TitleRegion = LocalRectangle.FromPoints(TitleMinX, TitleMinY, TitleMaxX, TitleMaxY),
                DrawingNumberRegion = LocalRectangle.FromPoints(NumberMinX, NumberMinY, NumberMaxX, NumberMaxY),
                CreatedAt = CreatedAt == default ? DateTime.Now : CreatedAt,
                UpdatedAt = DateTime.Now
            };
        }
    }
}
