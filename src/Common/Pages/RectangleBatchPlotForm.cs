using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly BindingList<Row> _displayRows = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _outputDirectory = new();
    private readonly ComboBox _sortOrder = new();
    private readonly ComboBox _outputFormatCombo = new();
    private readonly ComboBox _savePathModeCombo = new();
    private readonly ComboBox _style = new();
    private readonly CheckBox _mergePdf = new();
    private readonly CheckBox _leaveMargin = new();
    private readonly NumericUpDown _marginInput = new();
    private readonly Label _status = new();
    private CancellationTokenSource? _printCts;
    private Button? _printButton;
    private Extents3d? _scanWindow;
    private TitleBlockScanScope? _lastScanScope;
    private bool _updating;
    private bool _updatingPrintSelection;
    private bool _viewSortedByHeader;
    private bool _outputDirectoryIsCustom;
    private int _viewSortColumnIndex = -1;
    private List<Row>? _pendingPrintToggleRows;
    private string _pngPlotDevice = "";
    private string _jpgPlotDevice = "";
    private string _dwfPlotDevice = "";

    public RectangleBatchPlotForm(Document document)
    {
        _document = document;
        _settings = AppSettingsStore.Load();
        _overlay = new TemporarySequenceOverlay(document);
        // 订阅红框删除事件：在 CAD 中 ERASE 红框即可同步删除列表中对应的矩形框行
        _overlay.FrameErased += OverlayFrameErased;
        InitializeComponents();
        LoadPlotOptions();
        FormClosed += (_, _) => _overlay.Clear();
    }

    private void InitializeComponents()
    {
        Text = "LA矩形框批量打印";
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ClientSize = new Size(UiLayout.Scale(900), UiLayout.Scale(520));
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiLayout.DefaultFont;
        BackColor = Color.FromArgb(245, 247, 250);
        var tips = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.Scale(126),
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(6)),
            BackColor = Color.FromArgb(245, 247, 250)
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(38)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));

        var actions = NewFlow();
        var scanCurrent = UiLayout.CreateButton("扫描当前图", 108);
        scanCurrent.Click += (_, _) => ScanCurrentDrawing();
        var scanWindow = UiLayout.CreateButton("框选扫描", 92);
        scanWindow.Click += (_, _) => ScanSelectedWindow();
        var refresh = UiLayout.CreateButton("重新识别", 88);
        refresh.Click += (_, _) => ReloadFrames();
        actions.Controls.Add(scanCurrent);
        actions.Controls.Add(scanWindow);
        actions.Controls.Add(Separator());
        actions.Controls.Add(refresh);
        actions.Controls.Add(new Label
        {
            Text = "右键条目可设为不打印、删除或批量改纸张",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), 0, 0)
        });
        tips.SetToolTip(scanCurrent, "扫描当前打开图纸中的全部符合纸张比例的矩形框。");
        tips.SetToolTip(scanWindow, "回到 CAD 框选区域，只识别框内矩形框。");
        tips.SetToolTip(refresh, "按上次扫描方式重新扫描，并刷新矩形框列表。");

        var outputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(52)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("浏览...", 84) + UiLayout.Scale(8)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(72)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(98)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(42)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(100)));
        outputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("开始打印", 98)));
        outputRow.Controls.Add(LabelFor("输出"), 0, 0);
        _outputDirectory.Dock = DockStyle.Fill;
        _outputDirectory.Text = SourceDirectory();
        _outputDirectory.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(4));
        _outputDirectory.TextChanged += (_, _) => RefreshOutputPaths();
        _outputDirectory.Leave += (_, _) => ApplyManuallyEnteredOutputDirectory();
        outputRow.Controls.Add(_outputDirectory, 1, 0);
        var browseButton = UiLayout.CreateButton("浏览...", 84);
        browseButton.Margin = new Padding(0, UiLayout.Scale(2), 0, UiLayout.Scale(2));
        browseButton.Click += (_, _) => ChooseOutputDirectory();
        outputRow.Controls.Add(browseButton, 2, 0);

        _savePathModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        // 第三行同时容纳保存路径、排序、合并和留白选项，适当缩短目录选择框。
        _savePathModeCombo.Width = UiLayout.Scale(170);
        _savePathModeCombo.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(3));
        _savePathModeCombo.SelectionChangeCommitted += (_, _) => ApplySelectedSavePathMode();
        tips.SetToolTip(browseButton, "选择自定义输出文件夹。");

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        options.Controls.Add(new Label
        {
            Text = "保存路径:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(8), UiLayout.Scale(8), 0)
        });
        options.Controls.Add(_savePathModeCombo);
        options.Controls.Add(new Label
        {
            Text = "排序方式:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(8), UiLayout.Scale(4), 0)
        });
        _sortOrder.DropDownStyle = ComboBoxStyle.DropDownList;
        _sortOrder.Height = UiLayout.ButtonHeight();
        _sortOrder.Width = UiLayout.Scale(180);
        _sortOrder.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(3));
        _sortOrder.Items.AddRange(new object[] { "从上到下，从左到右", "从左到右，从上到下" });
        _sortOrder.SelectedIndex = 0;
        _sortOrder.SelectedIndexChanged += (_, _) => SortRows();
        options.Controls.Add(_sortOrder);

        _mergePdf.Text = "合并 PDF";
        // 与图框块批打印共用上一次合并状态；首次使用时 AppSettings 默认值为 false。
        _mergePdf.Checked = _settings.MergePdf;
        _mergePdf.AutoSize = true;
        _mergePdf.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _mergePdf.Margin = new Padding(UiLayout.Scale(4), UiLayout.Scale(7), UiLayout.Scale(8), 0);
        options.Controls.Add(_mergePdf);

        _leaveMargin.Text = "周边留白";
        _leaveMargin.Checked = _settings.LeavePaperMargin;
        _leaveMargin.AutoSize = true;
        _leaveMargin.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        _leaveMargin.Margin = new Padding(UiLayout.Scale(4), UiLayout.Scale(7), 0, 0);
        _marginInput.DecimalPlaces = 1;
        _marginInput.Minimum = 0.1m;
        _marginInput.Maximum = 20m;
        _marginInput.Increment = 0.5m;
        _marginInput.Value = Math.Max(0.1m, (decimal)_settings.PaperMarginMm);
        _marginInput.Width = UiLayout.Scale(58);
        _marginInput.Enabled = _leaveMargin.Checked;
        _leaveMargin.CheckedChanged += (_, _) => _marginInput.Enabled = _leaveMargin.Checked;
        _leaveMargin.Text = "留白";
        var marginPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        marginPanel.Controls.Add(_leaveMargin);
        marginPanel.Controls.Add(_marginInput);
        marginPanel.Controls.Add(new Label { Text = "mm", AutoSize = true, Margin = new Padding(2, UiLayout.Scale(7), 0, 0) });
        options.Controls.Remove(_leaveMargin);
        options.Controls.Add(marginPanel);

        // 输出格式、CTB 与开始打印和输出目录放在第二行。
        outputRow.Controls.Add(LabelFor("输出格式"), 3, 0);
        _outputFormatCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _outputFormatCombo.Height = UiLayout.ButtonHeight();
        _outputFormatCombo.Dock = DockStyle.Fill;
        _outputFormatCombo.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), 0);
        _outputFormatCombo.SelectionChangeCommitted += (_, _) => UpdateOutputFormatUi();
        outputRow.Controls.Add(_outputFormatCombo, 4, 0);
        outputRow.Controls.Add(LabelFor("CTB"), 5, 0);
        _style.DropDownStyle = ComboBoxStyle.DropDownList;
        _style.Height = UiLayout.ButtonHeight();
        _style.Dock = DockStyle.Fill;
        _style.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), 0);
        // 用户手动切换 CTB 后立即写入设置，图框块/矩形框/单张打印都会读取同一份上次选择。
        _style.SelectionChangeCommitted += (_, _) => SaveCurrentPlotOptions();
        outputRow.Controls.Add(_style, 6, 0);

        _printButton = UiLayout.CreateButton("开始打印", 98);
        _printButton.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        _printButton.BackColor = Color.FromArgb(0, 120, 215);
        _printButton.ForeColor = Color.White;
        _printButton.FlatStyle = FlatStyle.Flat;
        _printButton.FlatAppearance.BorderColor = Color.FromArgb(0, 95, 170);
        _printButton.Click += (_, _) => PrintOrStop();
        outputRow.Controls.Add(_printButton, 7, 0);
        tips.SetToolTip(_sortOrder, "改变列表、红框编号和最终输出文件的顺序。");
        tips.SetToolTip(_mergePdf, "仅 PDF 可用；书签和按纸张大小分组合并可在批量打印设置中配置。");
        tips.SetToolTip(marginPanel, "勾选后按设定距离在纸张短边两侧留白，居中等比例缩小打印。");

        top.Controls.Add(actions, 0, 0);
        top.Controls.Add(outputRow, 0, 1);
        top.Controls.Add(options, 0, 2);

        UiLayout.StyleGrid(_grid, Font);
        _grid.BorderStyle = BorderStyle.None;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.DefaultCellStyle.Padding = new Padding(UiLayout.Scale(3), 0, UiLayout.Scale(3), 0);
        _grid.DataSource = _displayRows;
        var indexCol = new DataGridViewTextBoxColumn { HeaderText = "编号", Width = UiLayout.Scale(52), ReadOnly = true };
        _grid.Columns.Add(indexCol);
        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == indexCol.Index && e.RowIndex >= 0)
            {
                if (_grid.Rows[e.RowIndex].DataBoundItem is Row row)
                {
                    e.Value = (_displayRows.IndexOf(row) + 1).ToString();
                }
            }
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
        foreach (DataGridViewColumn column in _grid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        }
        _grid.DataBindingComplete += (_, _) => ConfigurePaperCells();
        _grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
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
            if (e.RowIndex >= 0 && e.RowIndex < _displayRows.Count)
            {
                _highlightedJobIndex = _rows.IndexOf(_displayRows[e.RowIndex]);
                try
                {
                    var selectedJobs = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
                    var targetJob = _displayRows[e.RowIndex].Job;
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
            TextAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, UiLayout.Scale(7), UiLayout.Scale(4), 0)
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

        _displayRows.Clear();
        foreach (var row in _rows)
        {
            _displayRows.Add(row);
        }
        _viewSortedByHeader = false;
        _viewSortColumnIndex = -1;
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

    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope(this);

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
        _viewSortColumnIndex = -1;
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
        RefreshDisplayRows();
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

        // 用矩形边沿重叠判断行列分组，替代旧的中心点+中位数间隙法
        // 同一行/列内图幅大小不同时也能正确分组（中心点法会把大图框和小图框分成两行）
        // 重叠 ≥ 较小矩形边长的 30% → 视为同一行/列

        // ── 并查集分组 ──
        var parent = Enumerable.Range(0, rows.Count).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { parent[Find(a)] = Find(b); }

        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = i + 1; j < rows.Count; j++)
            {
                var ri = rows[i].Job;
                var rj = rows[j].Job;
                if (horizontalFirst)
                {
                    // 列分组：X 区间重叠
                    var overlapX = Math.Min(ri.MaxX, rj.MaxX) - Math.Max(ri.MinX, rj.MinX);
                    var minW = Math.Min(ri.MaxX - ri.MinX, rj.MaxX - rj.MinX);
                    if (overlapX >= minW * 0.3) Union(i, j);
                }
                else
                {
                    // 行分组：Y 区间重叠
                    var overlapY = Math.Min(ri.MaxY, rj.MaxY) - Math.Max(ri.MinY, rj.MinY);
                    var minH = Math.Min(ri.MaxY - ri.MinY, rj.MaxY - rj.MinY);
                    if (overlapY >= minH * 0.3) Union(i, j);
                }
            }
        }

        // ── 按分组整理 ──
        var groups = rows.Select((r, i) => (Row: r, Group: Find(i)))
            .GroupBy(x => x.Group)
            .Select(g => g.Select(x => x.Row).ToList())
            .ToList();

        // ── 组内排序 ──
        foreach (var group in groups)
        {
            if (horizontalFirst)
                group.Sort((a, b) => CenterY(b.Job).CompareTo(CenterY(a.Job))); // 列内 Y 降序
            else
                group.Sort((a, b) => CenterX(a.Job).CompareTo(CenterX(b.Job))); // 行内 X 升序
        }

        // ── 组间排序 ──
        if (horizontalFirst)
            groups = groups.OrderBy(g => g.Average(r => CenterX(r.Job))).ToList();  // 列 X 升序
        else
            groups = groups.OrderByDescending(g => g.Average(r => CenterY(r.Job))).ToList(); // 行 Y 降序

        // ── 展平 ──
        var result = new List<Row>();
        foreach (var group in groups) result.AddRange(group);
        return result;
    }

    private static double CenterX(PlotJob job) => (job.MinX + job.MaxX) / 2d;
    private static double CenterY(PlotJob job) => (job.MinY + job.MaxY) / 2d;

    private void RefreshDisplayRows()
    {
        _displayRows.Clear();
        foreach (var row in _rows)
        {
            _displayRows.Add(row);
        }
        ConfigurePaperCells();
        _grid.Refresh();
    }

    private void GridColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count)
        {
            return;
        }

        if (_viewSortedByHeader && _viewSortColumnIndex == e.ColumnIndex)
        {
            _viewSortedByHeader = false;
            _viewSortColumnIndex = -1;
            RefreshDisplayRows();
            return;
        }

        _viewSortedByHeader = true;
        _viewSortColumnIndex = e.ColumnIndex;
        var sorted = _rows.OrderBy(row => GetHeaderSortValue(row, e.ColumnIndex), NaturalStringComparer.Instance).ToList();
        _displayRows.Clear();
        foreach (var row in sorted)
        {
            _displayRows.Add(row);
        }
        ConfigurePaperCells();
        _grid.Refresh();
    }

    private string GetHeaderSortValue(Row row, int columnIndex)
    {
        var column = _grid.Columns[columnIndex];
        if (column.DataPropertyName == nameof(Row.Selected))
        {
            return row.Selected ? "1" : "0";
        }

        if (column.DataPropertyName == nameof(Row.FileName))
        {
            return row.FileName;
        }

        if (column.Name == "PaperChoice" || column.DataPropertyName == nameof(Row.PaperChoice))
        {
            return row.PaperChoice;
        }

        if (column.DataPropertyName == nameof(Row.Scale))
        {
            return row.Scale;
        }

        if (column.DataPropertyName == nameof(Row.GraphicSize))
        {
            return row.GraphicSize;
        }

        // 编号列按真实打印顺序排序；预览按钮列保持原始顺序。
        return _rows.IndexOf(row).ToString("D8");
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
        if (_updating || _updatingPrintSelection || e.RowIndex < 0 || e.ColumnIndex < 0
            || _grid.Rows[e.RowIndex].DataBoundItem is not Row row)
        {
            return;
        }

        if (_grid.Columns[e.ColumnIndex].DataPropertyName == nameof(Row.Selected))
        {
            ApplyPrintSelectionToHighlightedRows(row);
            RemoveUnselectedRows();
        }
        else if (_grid.Columns[e.ColumnIndex].Name == "PaperChoice")
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

        _grid.Refresh();
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        var changePaper = new ToolStripMenuItem("批量修改纸张...");
        changePaper.Click += (_, _) => BatchChangeHighlightedPaper();
        var markNotPrint = new ToolStripMenuItem("不打印");
        markNotPrint.Click += (_, _) => MarkHighlightedNotPrint();
        var delete = new ToolStripMenuItem("删除");
        delete.Click += (_, _) => DeleteHighlighted();
        menu.Items.Add(changePaper);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(markNotPrint);
        menu.Items.Add(delete);
        menu.Opening += (_, _) =>
        {
            // 只有多选行的候选纸张完全一致时，才允许统一修改纸张，避免套用到不适配的矩形框。
            changePaper.Enabled = TryGetCommonPaperOptions(HighlightedRows(), out _);
        };
        return menu;
    }

    private void GridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.RowIndex < 0)
        {
            return;
        }

        if (e.Button == MouseButtons.Left
            && e.ColumnIndex >= 0
            && _grid.Columns[e.ColumnIndex].DataPropertyName == nameof(Row.Selected)
            && _grid.SelectedRows.Count > 1
            && _grid.Rows[e.RowIndex].DataBoundItem is Row clickedRow)
        {
            // 先记住点击前的多选行；DataGrid 点击复选框时可能会先改当前选择，后续 CellValueChanged 再统一同步这些行。
            var highlightedRows = HighlightedRows();
            _pendingPrintToggleRows = highlightedRows.Contains(clickedRow) ? highlightedRows : null;
        }

        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        if (!_grid.Rows[e.RowIndex].Selected)
        {
            if (_grid.SelectedRows.Count <= 1)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
            // 已经 Shift/Ctrl 多选后，即使鼠标移到其它行右键，也保留原多选集合用于批量操作。
        }

        if (_grid.Rows[e.RowIndex].Selected)
        {
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(e.ColumnIndex, 0)];
        }
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
        RemoveUnselectedRows();
        RefreshFileNames();
        UpdateVisuals();
    }

    private void BatchChangeHighlightedPaper()
    {
        _grid.EndEdit();
        var rows = HighlightedRows();
        if (!TryGetCommonPaperOptions(rows, out var options))
        {
            MessageBox.Show("只有所选矩形框的候选纸张尺寸完全一致时，才能批量修改纸张。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SinglePlotPaperSelectionForm(options);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var selectedPaper = dialog.SelectedPaper;
        var paperChoice = FormatPaper(selectedPaper);
        foreach (var row in rows)
        {
            row.PaperChoice = paperChoice;
            ApplyPaper(row.Job, selectedPaper);
        }

        _grid.Refresh();
        ConfigurePaperCells();
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
            && first.IsLong == second.IsLong;
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

    // CAD 中红框被 ERASE 删除后的回调：同步从矩形框清单中移除对应行
    private void OverlayFrameErased(PlotJob job)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        void RemoveRow()
        {
            var row = _rows.FirstOrDefault(candidate => ReferenceEquals(candidate.Job, job));
            if (row == null)
            {
                return;
            }

            _rows.Remove(row);
            _highlightedJobIndex = -1;
            // 移除后重新编号、刷新文件名并重绘覆盖层，保持列表与 CAD 红框一致
            RefreshDisplayRows();
            RefreshFileNames();
            UpdateVisuals();
        }

        // CAD 事件可能来自非 UI 线程，需切回窗体线程再操作绑定列表
        if (InvokeRequired)
        {
            BeginInvoke((Action)RemoveRow);
        }
        else
        {
            RemoveRow();
        }
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
        else
        {
            _grid.Refresh();
        }
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
            if (IsDwgOutput)
            {
                MessageBox.Show("DWG 输出为拆图操作，不提供打印预览。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 预览与正式输出使用同一绘图器，避免不同格式之间出现纸张或旋转差异。
            var device = SelectedDevice();
            if (string.IsNullOrWhiteSpace(device))
            {
                MessageBox.Show($"未找到可用的 {SelectedOutputFormat} 输出设备。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var selectedRows = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
            var currentCell = _grid.CurrentCell;
            Hide();
            System.Windows.Forms.Application.DoEvents();
            try
            {
                SaveCurrentPlotOptions();
                row.Job.LeavePaperMargin = _leaveMargin.Checked;
                row.Job.PaperMarginMm = (double)_marginInput.Value;
                PlotterService.Preview(row.Job, device, SelectedStyle(), _document);
            }
            catch (Exception ex)
            {
                MessageBox.Show("打印预览失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Show();
                Activate();
                RestoreGridSelection(selectedRows, currentCell);
            }
        }
    }

    private void RestoreGridSelection(IReadOnlyList<DataGridViewRow> selectedRows, DataGridViewCell? currentCell)
    {
        try
        {
            _grid.ClearSelection();
            foreach (var row in selectedRows)
            {
                if (row.Index >= 0 && row.Index < _grid.Rows.Count)
                {
                    row.Selected = true;
                }
            }

            if (currentCell != null
                && currentCell.RowIndex >= 0 && currentCell.RowIndex < _grid.Rows.Count
                && currentCell.ColumnIndex >= 0 && currentCell.ColumnIndex < _grid.Columns.Count)
            {
                _grid.CurrentCell = _grid.Rows[currentCell.RowIndex].Cells[currentCell.ColumnIndex];
            }
        }
        catch
        {
            // CAD 预览退出后可能触发 DataGrid 选择状态变化；恢复失败不影响后续打印。
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
        _grid.EndEdit();
        RefreshOutputPaths();
        if (IsDwgOutput)
        {
            SplitDwgs();
            return;
        }

        var selected = _rows.Where(row => row.Selected).Select(row => row.Job).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有勾选任何矩形框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("请选择输出路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var device = SelectedDevice();
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show($"未找到可用的 {SelectedOutputFormat} 输出设备。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Directory.CreateDirectory(directory);
        SaveCurrentPlotOptions();
        ApplyLeaveMarginSelection(selected);
        var originalPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedOutput = Path.Combine(directory, SourceStem() + ".pdf");
        var mergePdf = IsPdfOutput && _mergePdf.Checked;
        var mergedOutputPaths = new List<string>();
        var completed = 0;
        try
        {
            // 切换按钮为"停止"状态
            _printCts = new CancellationTokenSource();
            if (_printButton != null)
            {
                _printButton.Text = "停止";
                _printButton.BackColor = Color.FromArgb(200, 40, 40);
                _printButton.FlatAppearance.BorderColor = Color.FromArgb(160, 30, 30);
            }

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
            System.Windows.Forms.Application.DoEvents();

            var results = PlotterService.PlotMany(
                selected, device, SelectedStyle(), _document, _settings,
                beforeJob: _ =>
                {
                    completed++;
                    _status.Text = $"打印中... {completed} / {selected.Count}";
                    System.Windows.Forms.Application.DoEvents();
                },
                cancellationToken: _printCts.Token);

            var failures = results.Where(result => !result.Succeeded).ToList();
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join("\n", failures.Select(result => result.Error?.Message)));
            }

            if (mergePdf)
            {
                _status.Text = "正在合并 PDF...";
                System.Windows.Forms.Application.DoEvents();
                var mergeInputs = selected.Select(job => new PdfMergeInput(
                    job.OutputPath,
                    Path.GetFileNameWithoutExtension(originalPaths[job]),
                    job.PaperName,
                    job.PaperWidthMm,
                    job.PaperHeightMm)).ToList();
                var mergePlans = PdfDocumentService.PlanMerges(
                    mergeInputs,
                    mergedOutput,
                    _settings.MergePdfByPaperSize);
                foreach (var mergePlan in mergePlans)
                {
                    PdfDocumentService.Merge(
                        mergePlan.Inputs,
                        mergePlan.OutputPath,
                        _settings.UseFileNameAsPdfBookmark);
                    mergedOutputPaths.Add(mergePlan.OutputPath);
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
            MessageBox.Show(
                mergePdf
                    ? $"打印并合并完成，共 {selected.Count} 张，生成 {mergedOutputPaths.Count} 个 PDF。\n{string.Join("\n", mergedOutputPaths)}"
                    : $"打印完成，共 {selected.Count} 张。\n{directory}",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _status.Text = $"已停止（已完成 {completed} / {selected.Count}）";
            MessageBox.Show($"打印已停止。\n已完成 {completed} / {selected.Count} 张。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "打印失败";
            MessageBox.Show("矩形框批量打印失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _printCts?.Dispose();
            _printCts = null;
            // 恢复按钮
            if (_printButton != null)
            {
                _printButton.Text = "开始打印";
                _printButton.BackColor = Color.FromArgb(0, 120, 215);
                _printButton.FlatAppearance.BorderColor = Color.FromArgb(0, 95, 170);
            }

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
            MessageBox.Show("没有勾选任何矩形框。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var directory = _outputDirectory.Text.Trim();
        if (string.IsNullOrWhiteSpace(directory))
        {
            MessageBox.Show("请选择输出路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"将按当前矩形框拆出 {selected.Count} 个 DWG 文件。\n\n输出位置：{directory}\n\n是否继续？",
            "矩形框批量拆图",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        SaveCurrentPlotOptions();
        Cursor = Cursors.WaitCursor;
        Enabled = false;
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
                    System.Windows.Forms.Application.DoEvents();
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
            MessageBox.Show($"DWG 拆图完成，共 {selected.Count} 张。\n{directory}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _status.Text = "拆图失败";
            MessageBox.Show("矩形框批量拆图失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            Enabled = true;
            Cursor = Cursors.Default;
            UpdateVisuals();
        }
    }

    private void ApplyLeaveMarginSelection(IEnumerable<PlotJob> jobs)
    {
        var leaveMargin = _leaveMargin.Checked;
        var marginMm = (double)_marginInput.Value;
        foreach (var job in jobs)
        {
            // 留白是本次输出选项，预览和正式打印都写入同一个 PlotJob，保证效果一致。
            job.LeavePaperMargin = leaveMargin;
            job.PaperMarginMm = marginMm;
        }
    }

    private void LoadPlotOptions()
    {
        _outputFormatCombo.Items.Clear();
        _outputFormatCombo.Items.AddRange(new object[] { "PDF", "PNG", "JPG", "DWF", "DWG" });
        _outputFormatCombo.SelectedIndex = 0;
        RefreshSavePathModeOptions(preserveSelection: false);

        AcadPlotterInstaller.InstallBundledPlotter();
        var installedPngPlotter = AcadPlotterInstaller.InstallPngPlotter();
        var installedJpgPlotter = AcadPlotterInstaller.InstallJpgPlotter();
        var installedDwfPlotter = AcadPlotterInstaller.InstallDwfPlotter();
        AcadPlotterInstaller.RefreshPlotterDevices();
        var validator = PlotSettingsValidator.Current;
        var devices = validator.GetPlotDeviceList()
            .Cast<object>()
            .Select(item => item?.ToString() ?? "")
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        _pngPlotDevice = FindPlotDevice(
            devices,
            installedPngPlotter,
            _ => false,
            AcadPlotterInstaller.PreferredPngPlotter);
        _jpgPlotDevice = FindPlotDevice(
            devices,
            installedJpgPlotter,
            _ => false,
            AcadPlotterInstaller.PreferredJpgPlotter);
        _dwfPlotDevice = FindPlotDevice(
            devices,
            installedDwfPlotter,
            value => value.IndexOf("DWF", StringComparison.OrdinalIgnoreCase) >= 0
                     && value.IndexOf("DWFx", StringComparison.OrdinalIgnoreCase) < 0,
            AcadPlotterInstaller.PreferredDwfPlotter,
            "DWF6 ePlot.pc3",
            "DWF6 ePlot.pc5",
            "ZWPLOT_DWF.pc5",
            "M_DWF.pc5");
        foreach (var item in validator.GetPlotStyleSheetList())
        {
            if (item is string value && value.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
            {
                _style.Items.Add(value);
            }
        }
        SelectOption(_style, _settings.LastStyleSheet, "monochrome");
        UpdateOutputFormatUi();
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
        _status.Text = $"识别 {_rows.Count} 个矩形框  |  已选 {selected} 个  |  格式：{SelectedOutputFormat}  |  顺序：{order}  |  输出：{_outputDirectory.Text}";
        try
        {
            var selectedJobs = _displayRows.Where(row => row.Selected).Select(row => row.Job).ToList();
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

    private void ChooseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择输出目录",
            SelectedPath = _outputDirectory.Text
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputDirectoryIsCustom = true;
            _outputDirectory.Text = dialog.SelectedPath;
            _outputDirectory.Modified = false;
            SaveCurrentPlotOptions();
            RefreshOutputPaths();
        }
    }

    private void ApplyManuallyEnteredOutputDirectory()
    {
        if (!_outputDirectory.Modified)
        {
            return;
        }

        _outputDirectory.Modified = false;
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

        _savePathModeCombo.BeginUpdate();
        try
        {
            _savePathModeCombo.Items.Clear();
            _savePathModeCombo.Items.Add("源文件路径");
            _savePathModeCombo.Items.Add(formatPathText);
            _savePathModeCombo.SelectedIndex = selectedIndex;
        }
        finally
        {
            _savePathModeCombo.EndUpdate();
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
        _outputDirectory.Text = string.IsNullOrWhiteSpace(subfolder)
            ? SourceDirectory()
            : Path.Combine(SourceDirectory(), subfolder);
        _outputDirectory.Modified = false;
    }

    private void UpdateOutputFormatUi()
    {
        RefreshSavePathModeOptions(preserveSelection: true);
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var plotOutput = !IsDwgOutput;
        _style.Enabled = plotOutput;
        _leaveMargin.Enabled = plotOutput;
        _marginInput.Enabled = plotOutput && _leaveMargin.Checked;
        _mergePdf.Enabled = IsPdfOutput;
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
    private bool IsJpgOutput => string.Equals(SelectedOutputFormat, "JPG", StringComparison.OrdinalIgnoreCase);
    private bool IsDwfOutput => string.Equals(SelectedOutputFormat, "DWF", StringComparison.OrdinalIgnoreCase);
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
        _settings.LastStyleSheet = SelectedStyle();
        _settings.MergePdf = _mergePdf.Checked;
        _settings.LeavePaperMargin = _leaveMargin.Checked;
        _settings.PaperMarginMm = (double)_marginInput.Value;
        AppSettingsStore.Save(_settings);
    }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 窗体销毁时退订事件并释放覆盖层（内部会退订 CAD 文档事件），防止关窗后仍拦截 ERASE 命令
            _overlay.FrameErased -= OverlayFrameErased;
            _overlay.Dispose();
        }

        base.Dispose(disposing);
    }
}
