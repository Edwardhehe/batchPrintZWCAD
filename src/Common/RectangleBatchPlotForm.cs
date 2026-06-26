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
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

public sealed class RectangleBatchPlotForm : Form
{
    private sealed class Row
    {
        public PlotJob Job { get; set; } = new();
        public IReadOnlyList<PaperDetection> Options { get; set; } = new PaperDetection[0];
        public bool Selected { get => Job.Selected; set => Job.Selected = value; }
        public string FileName { get; set; } = "";
        public string PaperChoice { get; set; } = "";
        public string Scale => Job.ScaleText;
        public string GraphicSize => Job.SizeText;
    }

    private readonly Document _document;
    private readonly AppSettings _settings;
    private readonly TemporarySequenceOverlay _overlay;
    private int _highlightedJobIndex = -1;
    private readonly BindingList<Row> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _outputDirectory = new();
    private readonly ComboBox _sortOrder = new();
    private readonly ComboBox _device = new();
    private readonly ComboBox _style = new();
    private readonly CheckBox _mergePdf = new();
    private readonly Label _status = new();
    private Extents3d? _scanWindow;
    private TitleBlockScanScope? _lastScanScope;
    private bool _updating;

    public RectangleBatchPlotForm(Document document)
    {
        _document = document;
        _settings = AppSettingsStore.Load();
        _overlay = new TemporarySequenceOverlay(document);
        InitializeComponents();
        LoadPlotOptions();
        FormClosed += (_, _) => _overlay.Clear();
    }

    private void InitializeComponents()
    {
        Text = "批量打印(选矩形框)";
        UiLayout.ConfigureBatchPlotForm(this);
        BackColor = Color.FromArgb(245, 247, 250);
        var tips = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.Scale(202),
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(6)),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(76)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(68)));

        var actions = NewFlow();
        var scanCurrent = UiLayout.CreateButton("扫描当前图", 108);
        scanCurrent.Click += (_, _) => ScanCurrentDrawing();
        var scanWindow = UiLayout.CreateButton("框选扫描", 92);
        scanWindow.Click += (_, _) => ScanSelectedWindow();
        var selectAll = UiLayout.CreateButton("全选", 64);
        selectAll.Click += (_, _) => SetAll(true);
        var selectNone = UiLayout.CreateButton("全不选", 76);
        selectNone.Click += (_, _) => SetAll(false);
        var refresh = UiLayout.CreateButton("重新识别", 88);
        refresh.Click += (_, _) => ReloadFrames();
        actions.Controls.Add(scanCurrent);
        actions.Controls.Add(scanWindow);
        actions.Controls.Add(Separator());
        actions.Controls.Add(selectAll);
        actions.Controls.Add(selectNone);
        actions.Controls.Add(Separator());
        actions.Controls.Add(refresh);
        actions.Controls.Add(new Label
        {
            Text = "右键条目可设为不打印或删除",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), 0, 0)
        });
        tips.SetToolTip(scanCurrent, "扫描当前打开图纸中的全部符合纸张比例的矩形框。");
        tips.SetToolTip(scanWindow, "回到 CAD 框选区域，只识别框内矩形框。");
        tips.SetToolTip(refresh, "按上次扫描方式重新扫描，并刷新矩形框列表。");

        var outputGroup = new GroupBox
        {
            Text = "PDF 输出位置",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(5), UiLayout.Scale(10), UiLayout.Scale(5)),
            Margin = new Padding(0, 0, 0, UiLayout.Scale(5))
        };
        var outputLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(27)));
        outputLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        _outputDirectory.Dock = DockStyle.Fill;
        _outputDirectory.Text = Path.Combine(SourceDirectory(), "PDF");
        _outputDirectory.Margin = new Padding(0, 0, 0, UiLayout.Scale(2));
        _outputDirectory.TextChanged += (_, _) => RefreshOutputPaths();
        outputLayout.Controls.Add(_outputDirectory, 0, 0);

        var pathButtons = NewFlow();
        var sourceButton = UiLayout.CreateButton("源文件路径", 98);
        sourceButton.Click += (_, _) => SetOutputDirectory(SourceDirectory());
        var pdfButton = UiLayout.CreateButton("源文件路径/PDF", 126);
        pdfButton.Click += (_, _) => SetOutputDirectory(Path.Combine(SourceDirectory(), "PDF"));
        var customButton = UiLayout.CreateButton("指定路径...", 88);
        customButton.Click += (_, _) => ChooseOutputDirectory();
        pathButtons.Controls.Add(sourceButton);
        pathButtons.Controls.Add(pdfButton);
        pathButtons.Controls.Add(customButton);
        outputLayout.Controls.Add(pathButtons, 0, 1);
        outputGroup.Controls.Add(outputLayout);
        tips.SetToolTip(sourceButton, "输出到当前 DWG 所在文件夹。");
        tips.SetToolTip(pdfButton, "输出到当前 DWG 所在文件夹下的 PDF 子文件夹。");
        tips.SetToolTip(customButton, "选择其他 PDF 输出文件夹。");

        var printGroup = new GroupBox
        {
            Text = "打印设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(5), UiLayout.Scale(10), UiLayout.Scale(5)),
            Margin = Padding.Empty
        };
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, RowCount = 1, Margin = Padding.Empty };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(42)));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(205)));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(128)));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(58)));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(38)));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(112)));

        options.Controls.Add(LabelFor("排序"), 0, 0);
        _sortOrder.DropDownStyle = ComboBoxStyle.DropDownList;
        _sortOrder.Dock = DockStyle.Fill;
        _sortOrder.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(4));
        _sortOrder.Items.AddRange(new object[] { "从上到下，从左到右", "从左到右，从上到下" });
        _sortOrder.SelectedIndex = 0;
        _sortOrder.SelectedIndexChanged += (_, _) => SortRows();
        options.Controls.Add(_sortOrder, 1, 0);

        _mergePdf.Text = "合并为一个 PDF";
        _mergePdf.Checked = true;
        _mergePdf.AutoSize = true;
        _mergePdf.Margin = new Padding(UiLayout.Scale(4), UiLayout.Scale(7), UiLayout.Scale(8), 0);
        options.Controls.Add(_mergePdf, 2, 0);
        options.Controls.Add(LabelFor("打印机"), 3, 0);
        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        _device.Dock = DockStyle.Fill;
        _device.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(4));
        options.Controls.Add(_device, 4, 0);
        options.Controls.Add(LabelFor("CTB"), 5, 0);
        _style.DropDownStyle = ComboBoxStyle.DropDownList;
        _style.Dock = DockStyle.Fill;
        _style.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(4));
        options.Controls.Add(_style, 6, 0);

        var print = UiLayout.CreateButton("开始打印", 98);
        print.Dock = DockStyle.Fill;
        print.Margin = new Padding(UiLayout.Scale(6), UiLayout.Scale(2), 0, UiLayout.Scale(4));
        print.BackColor = Color.FromArgb(0, 120, 215);
        print.ForeColor = Color.White;
        print.FlatStyle = FlatStyle.Flat;
        print.FlatAppearance.BorderColor = Color.FromArgb(0, 95, 170);
        print.Click += (_, _) => Print();
        options.Controls.Add(print, 7, 0);
        printGroup.Controls.Add(options);
        tips.SetToolTip(_sortOrder, "改变列表、红框编号和最终 PDF 页面的顺序。");
        tips.SetToolTip(_mergePdf, "勾选后只保留一个合并 PDF；取消后输出每张单独 PDF。");

        top.Controls.Add(actions, 0, 0);
        top.Controls.Add(outputGroup, 0, 1);
        top.Controls.Add(printGroup, 0, 2);

        UiLayout.StyleGrid(_grid, Font);
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.DefaultCellStyle.Padding = new Padding(UiLayout.Scale(3), 0, UiLayout.Scale(3), 0);
        _grid.DataSource = _rows;
        var indexCol = new DataGridViewTextBoxColumn { HeaderText = "编号", Width = UiLayout.Scale(52), ReadOnly = true };
        _grid.Columns.Add(indexCol);
        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == indexCol.Index && e.RowIndex >= 0)
                e.Value = (e.RowIndex + 1).ToString();
        };

        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "Preview", HeaderText = "预览", Text = "预览", UseColumnTextForButtonValue = true, Width = UiLayout.Scale(62)
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            DataPropertyName = nameof(Row.Selected), HeaderText = "打印", Width = UiLayout.Scale(62)
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.FileName), HeaderText = "文件名",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 34, MinimumWidth = UiLayout.Scale(170)
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "PaperChoice", DataPropertyName = nameof(Row.PaperChoice), HeaderText = "纸张尺寸 / 比例",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 34, MinimumWidth = UiLayout.Scale(200),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton, FlatStyle = FlatStyle.Flat
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.Scale), HeaderText = "比例", ReadOnly = true, Width = UiLayout.Scale(86)
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(Row.GraphicSize), HeaderText = "图形尺寸", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 22, MinimumWidth = UiLayout.Scale(130)
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
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _rows.Count)
            {
                _highlightedJobIndex = e.RowIndex;
                try
                {
                    var selectedJobs = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
                    var targetJob = _rows[e.RowIndex].Job;
                    var idx = selectedJobs.FindIndex(j => ReferenceEquals(j, targetJob));
                    _overlay.Show(selectedJobs, idx);
                }
                catch { }
            }
        };
        _grid.CellFormatting += GridCellFormatting;
        _grid.ContextMenuStrip = CreateContextMenu();
        _grid.DataError += (_, e) => e.ThrowException = false;

        _status.Dock = DockStyle.Bottom;
        _status.Height = UiLayout.Scale(30);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);
        _status.BackColor = Color.FromArgb(235, 239, 244);
        _status.ForeColor = Color.FromArgb(55, 65, 81);
        _status.BorderStyle = BorderStyle.FixedSingle;
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
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, UiLayout.Scale(4), 0)
        };

        static Control Separator() => new Label
        {
            Width = UiLayout.Scale(1),
            Height = UiLayout.ButtonHeight() - UiLayout.Scale(8),
            BackColor = Color.FromArgb(205, 210, 216),
            Margin = new Padding(UiLayout.Scale(4), UiLayout.Scale(6), UiLayout.Scale(12), UiLayout.Scale(4))
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
            List<RectangleFrameScanner.Result> results;
            if (_lastScanScope.HasValue)
            {
                results = RectangleFrameScanner.ScanScope(_document, _lastScanScope.Value);
            }
            else if (_scanWindow.HasValue)
            {
                results = RectangleFrameScanner.ScanWindow(_document, _scanWindow.Value);
            }
            else
            {
                return;
            }

            if (results.Count == 0)
            {
                MessageBox.Show("重新识别后没有找到矩形框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TransformResultsToDcs(results);
            LoadRows(results);
        }
        catch (Exception ex)
        {
            MessageBox.Show("重新识别矩形框失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private TitleBlockScanScope? PromptScanScope()
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
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
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
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
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

        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return null;
        }

        if (all.Checked) return TitleBlockScanScope.AllSpaces;
        if (layouts.Checked) return TitleBlockScanScope.PaperLayouts;
        if (current.Checked) return TitleBlockScanScope.CurrentSpace;
        return TitleBlockScanScope.ModelSpace;
    }

    private void ScanCurrentDrawing()
    {
        var scope = PromptScanScope();
        if (scope == null)
        {
            return;
        }

        try
        {
            var results = RectangleFrameScanner.ScanScope(_document, scope.Value);
            if (results.Count == 0)
            {
                MessageBox.Show("扫描范围内没有识别到符合常见纸张比例的矩形框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TransformResultsToDcs(results);
            _lastScanScope = scope;
            _scanWindow = null;
            LoadRows(results);
        }
        catch (Exception ex)
        {
            MessageBox.Show("扫描当前图失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ScanSelectedWindow()
    {
        Hide();
        System.Windows.Forms.Application.DoEvents();
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

            var ucsToWcs = editor.CurrentUserCoordinateSystem;
            var ucsX1 = first.Value.X;
            var ucsY1 = first.Value.Y;
            var ucsX2 = second.Value.X;
            var ucsY2 = second.Value.Y;
            var wcsCorners = new[]
            {
                new Point3d(ucsX1, ucsY1, 0).TransformBy(ucsToWcs),
                new Point3d(ucsX2, ucsY1, 0).TransformBy(ucsToWcs),
                new Point3d(ucsX1, ucsY2, 0).TransformBy(ucsToWcs),
                new Point3d(ucsX2, ucsY2, 0).TransformBy(ucsToWcs)
            };
            var window = new Extents3d(
                new Point3d(wcsCorners.Min(p => p.X), wcsCorners.Min(p => p.Y), 0),
                new Point3d(wcsCorners.Max(p => p.X), wcsCorners.Max(p => p.Y), 0));

            var results = RectangleFrameScanner.ScanWindow(_document, window);
            if (results.Count == 0)
            {
                MessageBox.Show("框选范围内没有识别到符合常见纸张比例的矩形框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            TransformResultsToDcs(results);
            _scanWindow = window;
            _lastScanScope = null;
            LoadRows(results);
        }
        catch (Exception ex)
        {
            MessageBox.Show("框选扫描失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Show();
            Activate();
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
                if (result.CornerPoints != null)
                {
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
            UpdateVisuals();
            return;
        }

        var horizontalFirst = _sortOrder.SelectedIndex == 1;

        // 多布局按 TabOrder 分组（Scanner 已按 TabOrder 排序），组内空间排序，组间保持布局顺序
        var layoutOrder = _rows
            .Select(r => r.Job.SpaceName)
            .Distinct()
            .ToList();
        var allRows = _rows.ToList();
        _updating = true;
        _rows.Clear();
        foreach (var spaceName in layoutOrder)
        {
            var group = allRows
                .Where(r => string.Equals(r.Job.SpaceName, spaceName, StringComparison.Ordinal))
                .ToList();
            var sorted = SortSpatially(group, horizontalFirst);
            foreach (var row in sorted)
            {
                _rows.Add(row);
            }
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

        // 用相邻帧中心点间距的中位数的一半作为行列容差
        // 比固定比例（0.35 × 图框尺寸）更能适应不同间距和微偏移
        // 行先：取 Y 中心间距；列先：取 X 中心间距
        var centers = horizontalFirst
            ? rows.Select(row => CenterX(row.Job)).Distinct().OrderBy(x => x).ToList()
            : rows.Select(row => CenterY(row.Job)).Distinct().OrderBy(y => y).ToList();
        var gaps = centers.Zip(centers.Skip(1), (a, b) => Math.Abs(b - a))
            .Where(g => g > 1e-6).OrderBy(g => g).ToList();
        var medianGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 1.0;
        var bandTolerance = Math.Max(medianGap * 0.5, 1e-6);
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

    private void GridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not Row row)
        {
            return;
        }

        if (!row.Selected && !_grid.Rows[e.RowIndex].Selected)
        {
            var cellStyle = e.CellStyle;
            if (cellStyle is null)
            {
                return;
            }

            cellStyle.ForeColor = Color.Gray;
            cellStyle.BackColor = Color.FromArgb(247, 247, 247);
        }
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
        var selected = _rows.Count(row => row.Selected);
        var order = _sortOrder.SelectedIndex == 1 ? "左→右、上→下" : "上→下、左→右";
        _status.Text = $"识别 {_rows.Count} 个矩形框  |  打印 {selected} 个  |  顺序：{order}  |  输出：{_outputDirectory.Text}";
        try
        {
            var selectedJobs = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
            var highlightIdx = -1;
            if (_highlightedJobIndex >= 0 && _highlightedJobIndex < _rows.Count)
            {
                var targetJob = _rows[_highlightedJobIndex].Job;
                highlightIdx = selectedJobs.FindIndex(j => ReferenceEquals(j, targetJob));
            }
            _overlay.Show(selectedJobs, highlightIdx);
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
