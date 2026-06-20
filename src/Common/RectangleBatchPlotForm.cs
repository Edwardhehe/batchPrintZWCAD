using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
#endif

namespace ZwcadBatchPlot;

public sealed class RectangleBatchPlotForm : Form
{
    private sealed class Row
    {
        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> Options { get; set; } = Array.Empty<PaperDetection>();
        public bool Selected { get => Job.Selected; set => Job.Selected = value; }
        public string FileName { get; set; } = "";
        public string PaperChoice { get; set; } = "";
        public string Scale => Job.ScaleText;
        public string GraphicSize => Job.SizeText;
    }

    private readonly Document _document;
    private readonly AppSettings _settings;
    private readonly TemporarySequenceOverlay _overlay;
    private readonly BindingList<Row> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _outputDirectory = new();
    private readonly ComboBox _sortOrder = new();
    private readonly ComboBox _device = new();
    private readonly ComboBox _style = new();
    private readonly CheckBox _mergePdf = new();
    private readonly Label _status = new();
    private readonly Extents3d _scanWindow;
    private bool _updating;

    public RectangleBatchPlotForm(
        Document document,
        Extents3d scanWindow,
        IReadOnlyList<RectangleFrameScanner.Result> results)
    {
        _document = document;
        _scanWindow = scanWindow;
        _settings = AppSettingsStore.Load();
        _overlay = new TemporarySequenceOverlay(document);
        InitializeComponents();
        LoadPlotOptions();
        LoadRows(results);
        FormClosed += (_, _) => _overlay.Clear();
    }

    private void InitializeComponents()
    {
        Text = "批量打印(选矩形框)";
        UiLayout.ConfigureBatchPlotForm(this);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.Scale(176),
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(4))
        };

        var pathButtons = NewFlow();
        var sourceButton = UiLayout.CreateButton("源文件路径", 98);
        sourceButton.Click += (_, _) => SetOutputDirectory(SourceDirectory());
        var pdfButton = UiLayout.CreateButton("源文件路径/PDF", 126);
        pdfButton.Click += (_, _) => SetOutputDirectory(Path.Combine(SourceDirectory(), "PDF"));
        var customButton = UiLayout.CreateButton("指定路径", 88);
        customButton.Click += (_, _) => ChooseOutputDirectory();
        pathButtons.Controls.Add(sourceButton);
        pathButtons.Controls.Add(pdfButton);
        pathButtons.Controls.Add(customButton);

        var actions = NewFlow();
        var selectAll = UiLayout.CreateButton("全选", 64);
        selectAll.Click += (_, _) => SetAll(true);
        var selectNone = UiLayout.CreateButton("全不选", 76);
        selectNone.Click += (_, _) => SetAll(false);
        var refresh = UiLayout.CreateButton("重新识别", 88);
        refresh.Click += (_, _) => ReloadFrames();
        actions.Controls.Add(selectAll);
        actions.Controls.Add(selectNone);
        actions.Controls.Add(refresh);

        var options = NewFlow();
        options.Controls.Add(LabelFor("排序:"));
        _sortOrder.DropDownStyle = ComboBoxStyle.DropDownList;
        _sortOrder.Width = UiLayout.Scale(220);
        _sortOrder.Items.AddRange(new object[] { "从上到下，从左到右", "从左到右，从上到下" });
        _sortOrder.SelectedIndex = 0;
        _sortOrder.SelectedIndexChanged += (_, _) => SortRows();
        options.Controls.Add(_sortOrder);
        _mergePdf.Text = "合并为一个PDF";
        _mergePdf.Checked = true;
        _mergePdf.AutoSize = true;
        _mergePdf.Margin = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), 0);
        options.Controls.Add(_mergePdf);
        options.Controls.Add(LabelFor("打印机:"));
        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        _device.Width = UiLayout.Scale(170);
        options.Controls.Add(_device);
        options.Controls.Add(LabelFor("CTB:"));
        _style.DropDownStyle = ComboBoxStyle.DropDownList;
        _style.Width = UiLayout.Scale(135);
        options.Controls.Add(_style);
        var print = UiLayout.CreateButton("开始打印", 98);
        print.BackColor = Color.FromArgb(0, 120, 215);
        print.ForeColor = Color.White;
        print.Click += (_, _) => Print();
        options.Controls.Add(print);

        var path = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        path.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(72)));
        path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        path.Controls.Add(LabelFor("PDF路径:"), 0, 0);
        _outputDirectory.Dock = DockStyle.Fill;
        _outputDirectory.Text = Path.Combine(SourceDirectory(), "PDF");
        _outputDirectory.TextChanged += (_, _) => RefreshOutputPaths();
        path.Controls.Add(_outputDirectory, 1, 0);

        top.Controls.Add(options, 0, 0);
        top.Controls.Add(path, 0, 1);
        top.Controls.Add(pathButtons, 0, 2);
        top.Controls.Add(actions, 0, 3);

        UiLayout.StyleGrid(_grid, Font);
        _grid.DataSource = _rows;
        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Preview",
            HeaderText = "预览",
            Text = "预览",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(62)
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(Row.Selected),
            HeaderText = "是否打印",
            Width = UiLayout.Scale(78)
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.FileName),
            HeaderText = "文件名",
            Width = UiLayout.Scale(230)
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "PaperChoice",
            DataPropertyName = nameof(Row.PaperChoice),
            HeaderText = "纸张尺寸",
            Width = UiLayout.Scale(210),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Scale),
            HeaderText = "比例",
            ReadOnly = true,
            Width = UiLayout.Scale(95)
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.GraphicSize),
            HeaderText = "图形尺寸",
            ReadOnly = true,
            Width = UiLayout.Scale(165)
        });
        _grid.DataBindingComplete += (_, _) => ConfigurePaperCells();
        _grid.CellValueChanged += GridCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellContentClick += GridCellContentClick;
        _grid.CellMouseDown += GridCellMouseDown;
        _grid.ContextMenuStrip = CreateContextMenu();
        _grid.DataError += (_, e) => e.ThrowException = false;

        _status.Dock = DockStyle.Bottom;
        _status.Height = UiLayout.Scale(30);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);
        Controls.Add(_grid);
        Controls.Add(_status);
        Controls.Add(top);

        static FlowLayoutPanel NewFlow() => new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true
        };

        static Label LabelFor(string text) => new()
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(8), UiLayout.Scale(4), 0)
        };
    }

    private void LoadRows(IReadOnlyList<RectangleFrameScanner.Result> results)
    {
        _rows.Clear();
        foreach (var result in results)
        {
            var option = result.PaperOptions[0];
            _rows.Add(new Row
            {
                Job = result.Job,
                Options = result.PaperOptions,
                PaperChoice = FormatPaper(option)
            });
        }

        SortRows();
    }

    private void ReloadFrames()
    {
        try
        {
            LoadRows(RectangleFrameScanner.ScanWindow(_document, _scanWindow));
        }
        catch (Exception ex)
        {
            MessageBox.Show("重新识别矩形框失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SortRows()
    {
        if (_rows.Count == 0)
        {
            UpdateVisuals();
            return;
        }

        var horizontalFirst = _sortOrder.SelectedIndex == 1;
        var sorted = SortSpatially(_rows.ToList(), horizontalFirst);
        _updating = true;
        _rows.Clear();
        foreach (var row in sorted)
        {
            _rows.Add(row);
        }
        _updating = false;
        RefreshFileNames();
        ConfigurePaperCells();
        UpdateVisuals();
    }

    private static List<Row> SortSpatially(IReadOnlyList<Row> rows, bool horizontalFirst)
    {
        if (rows.Count <= 1)
        {
            return rows.ToList();
        }

        var typicalSpan = rows
            .Select(row => horizontalFirst
                ? Math.Abs(row.Job.MaxX - row.Job.MinX)
                : Math.Abs(row.Job.MaxY - row.Job.MinY))
            .Where(value => value > 1e-6)
            .OrderBy(value => value)
            .ElementAt(Math.Max(0, rows.Count / 2 - 1));
        var bandTolerance = Math.Max(typicalSpan * 0.35, 1e-6);
        var remaining = horizontalFirst
            ? rows.OrderBy(row => CenterX(row.Job)).ToList()
            : rows.OrderByDescending(row => CenterY(row.Job)).ToList();
        var result = new List<Row>();

        while (remaining.Count > 0)
        {
            var anchor = horizontalFirst ? CenterX(remaining[0].Job) : CenterY(remaining[0].Job);
            var band = remaining
                .Where(row => Math.Abs((horizontalFirst ? CenterX(row.Job) : CenterY(row.Job)) - anchor) <= bandTolerance)
                .ToList();
            foreach (var row in band)
            {
                remaining.Remove(row);
            }

            result.AddRange(horizontalFirst
                ? band.OrderByDescending(row => CenterY(row.Job))
                : band.OrderBy(row => CenterX(row.Job)));
        }

        return result;
    }

    private static double CenterX(PlotJob job) => (job.MinX + job.MaxX) / 2d;
    private static double CenterY(PlotJob job) => (job.MinY + job.MaxY) / 2d;

    private void RefreshFileNames()
    {
        var stem = Path.GetFileNameWithoutExtension(_document.Database.Filename);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = Path.GetFileNameWithoutExtension(_document.Name);
        }

        var printIndex = 0;
        for (var i = 0; i < _rows.Count; i++)
        {
            if (!_rows[i].Selected)
            {
                continue;
            }

            printIndex++;
            _rows[i].Job.DrawingNumber = printIndex.ToString("D2");
            _rows[i].FileName = $"{stem}{printIndex:D2}.pdf";
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
        _grid.Refresh();
    }

    private void ConfigurePaperCells()
    {
        foreach (DataGridViewRow gridRow in _grid.Rows)
        {
            if (gridRow.DataBoundItem is not Row row
                || gridRow.Cells["PaperChoice"] is not DataGridViewComboBoxCell cell)
            {
                continue;
            }

            cell.DataSource = row.Options.Select(FormatPaper).ToList();
            cell.Value = row.PaperChoice;
        }
    }

    private void GridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updating || e.RowIndex < 0 || e.ColumnIndex < 0
            || _grid.Rows[e.RowIndex].DataBoundItem is not Row row)
        {
            return;
        }

        if (_grid.Columns[e.ColumnIndex].Name == "PaperChoice")
        {
            var value = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? "";
            var option = row.Options.FirstOrDefault(candidate => string.Equals(FormatPaper(candidate), value, StringComparison.Ordinal));
            if (option != null)
            {
                row.PaperChoice = value;
                ApplyPaper(row.Job, option);
                _grid.Refresh();
            }
        }

        RefreshFileNames();
        RefreshOutputPaths();
        UpdateVisuals();
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        var markNotPrint = new ToolStripMenuItem("不打印");
        markNotPrint.Click += (_, _) => MarkHighlightedNotPrint();
        var delete = new ToolStripMenuItem("删除");
        delete.Click += (_, _) => DeleteHighlighted();
        menu.Items.Add(markNotPrint);
        menu.Items.Add(delete);
        return menu;
    }

    private void GridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0)
        {
            return;
        }

        if (!_grid.Rows[e.RowIndex].Selected)
        {
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
        }
        _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(e.ColumnIndex, 0)];
    }

    private List<Row> HighlightedRows()
    {
        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(gridRow => gridRow.DataBoundItem)
            .OfType<Row>()
            .Distinct()
            .ToList();
        if (rows.Count == 0 && _grid.CurrentRow?.DataBoundItem is Row current)
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
        _grid.Refresh();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void DeleteHighlighted()
    {
        foreach (var row in HighlightedRows())
        {
            _rows.Remove(row);
        }
        RefreshFileNames();
        ConfigurePaperCells();
        UpdateVisuals();
    }

    private void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        if (_grid.Columns[e.ColumnIndex].Name == "Preview"
            && _grid.Rows[e.RowIndex].DataBoundItem is Row row)
        {
            try
            {
                PlotterService.Preview(row.Job, SelectedDevice(), SelectedStyle(), _document);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打印预览失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void Print()
    {
        _grid.EndEdit();
        RefreshOutputPaths();
        var selected = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有勾选任何矩形框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("请选择 PDF 输出路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Directory.CreateDirectory(directory);
        var originalPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedOutput = Path.Combine(directory, SourceStem() + ".pdf");
        try
        {
            Hide();
            System.Windows.Forms.Application.DoEvents();
            if (_mergePdf.Checked)
            {
                temporaryDirectory = Path.Combine(Path.GetTempPath(), "ZwcadBatchPlot", "RectangleMerge_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporaryDirectory);
                for (var i = 0; i < selected.Count; i++)
                {
                    selected[i].OutputPath = Path.Combine(temporaryDirectory, $"{i + 1:D5}.pdf");
                }
            }

            var results = PlotterService.PlotMany(selected, SelectedDevice(), SelectedStyle(), _document, _settings);
            var failures = results.Where(result => !result.Succeeded).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", failures.Select(result => result.Error?.Message)));
            }

            if (_mergePdf.Checked)
            {
                PdfDocumentService.Merge(selected.Select(job => job.OutputPath).ToList(), mergedOutput);
            }

            RevealOutput(_mergePdf.Checked ? mergedOutput : null, directory);
            MessageBox.Show(
                _mergePdf.Checked
                    ? $"打印并合并完成，共 {selected.Count} 张。\n{mergedOutput}"
                    : $"打印完成，共 {selected.Count} 张。\n{directory}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("矩形框批量打印失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            foreach (var pair in originalPaths)
            {
                pair.Key.OutputPath = pair.Value;
            }
            if (!string.IsNullOrWhiteSpace(temporaryDirectory))
            {
                try { Directory.Delete(temporaryDirectory, true); } catch { }
            }
            Show();
            Activate();
            UpdateVisuals();
        }
    }

    private void LoadPlotOptions()
    {
        AcadPlotterInstaller.InstallBundledPlotter();
        using var settings = new PlotSettings(true);
        var validator = PlotSettingsValidator.Current;
        foreach (var item in validator.GetPlotDeviceList())
        {
            if (item is string value && !string.IsNullOrWhiteSpace(value))
            {
                _device.Items.Add(value);
            }
        }
        foreach (var item in validator.GetPlotStyleSheetList())
        {
            if (item is string value && value.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
            {
                _style.Items.Add(value);
            }
        }
        SelectOption(_device, AcadPlotterInstaller.PreferredPdfPlotter, _settings.LastPlotDevice, "PDF");
        SelectOption(_style, _settings.LastStyleSheet, "monochrome");
    }

    private static void SelectOption(ComboBox combo, params string[] preferred)
    {
        foreach (var expected in preferred.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                if ((combo.Items[i]?.ToString() ?? "").IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void SetAll(bool selected)
    {
        foreach (var row in _rows)
        {
            row.Selected = selected;
        }
        _grid.Refresh();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        _status.Text = $"识别矩形框 {_rows.Count} 个，勾选 {_rows.Count(row => row.Selected)} 个";
        try
        {
            _overlay.Show(_rows.Where(row => row.Selected).Select(row => row.Job).ToList());
        }
        catch
        {
            _overlay.Clear();
        }
    }

    private void SetOutputDirectory(string directory)
    {
        _outputDirectory.Text = directory;
        RefreshOutputPaths();
    }

    private void ChooseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 PDF 输出目录",
            SelectedPath = _outputDirectory.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetOutputDirectory(dialog.SelectedPath);
        }
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

    private string SelectedDevice() => _device.SelectedItem?.ToString() ?? "";
    private string SelectedStyle() => _style.SelectedItem?.ToString() ?? "";

    private static string FormatPaper(PaperDetection paper)
    {
        return $"{paper.PaperName} | {paper.PaperWidthMm:0.##}×{paper.PaperHeightMm:0.##}mm | {paper.ScaleText}";
    }

    private static void ApplyPaper(PlotJob job, PaperDetection paper)
    {
        job.PaperName = paper.PaperName;
        job.PaperWidthMm = paper.PaperWidthMm;
        job.PaperHeightMm = paper.PaperHeightMm;
        job.PaperSizeText = $"{paper.PaperWidthMm:0.##} x {paper.PaperHeightMm:0.##} mm";
        job.ScaleText = paper.ScaleText;
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
}
