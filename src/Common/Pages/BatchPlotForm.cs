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

public sealed class BatchPlotForm : Form
{
    private readonly Document _currentDocument;
    private readonly BindingList<PlotJob> _jobs = new();
    private readonly DataGridView _grid = new();
    private readonly TextBox _outputDirectory = new();
    private readonly ComboBox _deviceCombo = new();
    private readonly ComboBox _styleCombo = new();
    private readonly CheckBox _mergePdfCheckBox = new();
    private readonly CheckBox _addSeqCheckBox = new();
    private readonly Button _printButton = new();
    private readonly Label _statusLabel = new();
    private readonly List<string> _logLines = new();
    private readonly List<string> _selectedDwgFiles = new();
    private readonly TemporarySequenceOverlay _sequenceOverlay;
    private readonly AppSettings _settings;
    private bool _sequenceOverlayFollowsCurrentJobs;
    private PlotJob? _highlightedJob;
    private string _lastLogPath = "";
    private string _mergedOutputPath = "";
    private long _nextSortPriority;
    public bool HasPendingPrint { get; private set; }

    public BatchPlotForm(Document currentDocument)
    {
        _currentDocument = currentDocument;
        _sequenceOverlay = new TemporarySequenceOverlay(currentDocument);
        _settings = AppSettingsStore.Load();
        _settings.AutoScanCurrentDrawing = false;
        InitializeComponents();
        LoadPlotOptions();
        RefreshStatus();
    }

    private void InitializeComponents()
    {
#if AUTOCAD
        Text = "批量打印(选图框块) - AutoCAD";
#else
        Text = "批量打印(选图框块) - ZWCAD";
#endif
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ClientSize = new Size(UiLayout.Scale(900), UiLayout.Scale(560));
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiLayout.DefaultFont;
        var tips = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 450,
            ReshowDelay = 100,
            ShowAlways = true
        };

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.ActionPanelHeight(),
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(6)),
            BackColor = SystemColors.Control
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ActionButtonRowsHeight()));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(10)));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.ButtonHeight() + UiLayout.Scale(4)));

        var actionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty
        };

        var settingsRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 8,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        var pathRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
            Margin = Padding.Empty
        };
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(42)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("浏览...", 70) + UiLayout.Scale(6)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(54)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(170)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(36)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(130)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("开始打印", 86) + UiLayout.Scale(8)));

        Button MakeButton(string text, int width)
        {
            return UiLayout.CreateButton(text, width);
        }

        Label MakeLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = Padding.Empty
            };
        }

        var scanButton = MakeButton("扫描当前图", 108);
        scanButton.Click += (_, _) => ScanCurrentDrawing();

        var scanWindowButton = MakeButton("框选扫描", 92);
        scanWindowButton.Click += (_, _) => ScanSelectedWindow();

        var addFilesButton = MakeButton("添加DWG", 92);
        addFilesButton.Click += (_, _) => AddDwgFiles();

        var selectAllButton = MakeButton("全选", 68);
        selectAllButton.Click += (_, _) => SetAllSelected(true);

        var selectNoneButton = MakeButton("全不选", 78);
        selectNoneButton.Click += (_, _) => SetAllSelected(false);

        var invertButton = MakeButton("反选", 68);
        invertButton.Click += (_, _) => InvertSelected();

        var removeHighlightedButton = MakeButton("删除行", 82);
        removeHighlightedButton.Click += (_, _) => RemoveHighlightedJobs();

        var clearButton = MakeButton("清空清单", 92);
        clearButton.Click += (_, _) => ClearJobs();

        var renumberButton = MakeButton("图号重排", 92);
        renumberButton.Click += (_, _) => RenumberDrawingNumbers();

        var refreshNameButton = MakeButton("刷新文件名", 104);
        refreshNameButton.Click += (_, _) => SortAndRefreshOutputPaths();

        var exportCsvButton = MakeButton("导出清单", 92);
        exportCsvButton.Click += (_, _) => ExportCsv();

        var generateDirectoryButton = MakeButton("生成目录", 92);
        generateDirectoryButton.Click += (_, _) => GenerateDrawingDirectory();

        var splitDwgButton = MakeButton("批量拆图", 92);
        splitDwgButton.Click += (_, _) => SplitSelectedDwgs();

        var openLogButton = MakeButton("打开日志", 92);
        openLogButton.Click += (_, _) => OpenLastLog();

        var chooseOutputButton = MakeButton("浏览...", 84);
        chooseOutputButton.Click += (_, _) => ChooseOutputDirectory();

        var currentFolderButton = MakeButton("当前文件夹", 96);
        currentFolderButton.Click += (_, _) => SetOutputDirectory(GetSelectedCadDirectory());

        var currentPdfButton = MakeButton("当前文件夹/PDF", 116);
        currentPdfButton.Click += (_, _) => SetOutputDirectory(Path.Combine(GetSelectedCadDirectory(), "PDF"));

        var specifiedFolderButton = MakeButton("指定文件夹", 96);
        specifiedFolderButton.Click += (_, _) => ChooseOutputDirectory();

        _printButton.Text = "开始打印";
        _printButton.Width = UiLayout.ButtonWidth(_printButton.Text, 104);
        _printButton.Height = UiLayout.ButtonHeight();
        _printButton.Dock = DockStyle.Fill;
        _printButton.Margin = new Padding(UiLayout.Scale(8), UiLayout.Scale(2), 0, UiLayout.Scale(6));
        _printButton.BackColor = Color.FromArgb(0, 120, 215);
        _printButton.ForeColor = Color.White;
        _printButton.FlatStyle = FlatStyle.Flat;
        _printButton.FlatAppearance.BorderColor = Color.FromArgb(0, 95, 170);
        _printButton.UseVisualStyleBackColor = false;
        _printButton.Click += (_, _) => PrintSelectedJobs();
        tips.SetToolTip(_printButton, "打印当前勾选的图纸。");

        SetTip(scanButton, "扫描当前打开图纸中的全部匹配图框。");
        SetTip(scanWindowButton, "回到 CAD 框选区域，只识别框内图框。");
        SetTip(addFilesButton, "选择一个或多个 DWG，加入批量打印清单。");
        SetTip(selectAllButton, "勾选全部图纸用于打印。");
        SetTip(selectNoneButton, "取消全部打印勾选。");
        SetTip(invertButton, "反转打印勾选状态。");
        SetTip(removeHighlightedButton, "删除鼠标高亮的行，可 Ctrl/Shift 多选。");
        SetTip(clearButton, "清空当前清单，不影响 CAD 文件和图框库。");
        SetTip(renumberButton, "按空间位置重新排序图框，按顺序分配前缀+递增图号。");
        SetTip(refreshNameButton, "按当前图号、图名和设置重新生成输出 PDF 文件名。");
        SetTip(exportCsvButton, "导出当前清单为 CSV。");
        SetTip(generateDirectoryButton, "在当前 CAD 指定基点，生成图纸目录表。");
        SetTip(splitDwgButton, "按当前勾选图纸拆成单独 DWG。模型空间生成轻量新图，布局空间保留原模型并清理目标布局。");
        SetTip(openLogButton, "打开最近一次运行日志。");
        SetTip(chooseOutputButton, "选择 PDF 输出目录。");
        SetTip(currentFolderButton, "输出到所选 CAD 文件所在目录。");
        SetTip(currentPdfButton, "输出到所选 CAD 文件所在目录下的 PDF 文件夹。");
        SetTip(specifiedFolderButton, "手动指定 PDF 输出目录。");

        _outputDirectory.Dock = DockStyle.Fill;
        _outputDirectory.Margin = new Padding(0, UiLayout.Scale(4), UiLayout.Scale(8), UiLayout.Scale(8));
        _outputDirectory.Text = GetDefaultOutputDirectory();

        _deviceCombo.Dock = DockStyle.Fill;
        _deviceCombo.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(10), UiLayout.Scale(8));
        _deviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        _styleCombo.Dock = DockStyle.Fill;
        _styleCombo.Margin = new Padding(0, UiLayout.Scale(3), 0, UiLayout.Scale(8));
        _styleCombo.DropDownStyle = ComboBoxStyle.DropDownList;

        _mergePdfCheckBox.Text = "合并为单个 PDF";
        _mergePdfCheckBox.AutoSize = true;
        _mergePdfCheckBox.Checked = false;
        _mergePdfCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _mergePdfCheckBox.Margin = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), 0);
        SetTip(_mergePdfCheckBox, "勾选后选择最终文件名；打印过程只使用临时单页，完成后仅保留一个合并 PDF。");

        _addSeqCheckBox.Text = "文件名加序号";
        _addSeqCheckBox.AutoSize = true;
        _addSeqCheckBox.Checked = false;
        _addSeqCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _addSeqCheckBox.Margin = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), 0);
        _addSeqCheckBox.CheckedChanged += (_, _) => SortAndRefreshOutputPaths();
        SetTip(_addSeqCheckBox, "勾选后文件名前自动加序号，序号与图号的连接符使用设置中的连接符。");

        actionRow.Controls.Add(scanButton);
        actionRow.Controls.Add(scanWindowButton);
        actionRow.Controls.Add(addFilesButton);
        actionRow.Controls.Add(MakeSeparator());
        actionRow.Controls.Add(selectAllButton);
        actionRow.Controls.Add(selectNoneButton);
        actionRow.Controls.Add(invertButton);
        actionRow.Controls.Add(MakeSeparator());
        actionRow.Controls.Add(removeHighlightedButton);
        actionRow.Controls.Add(clearButton);
        actionRow.Controls.Add(MakeSeparator());
        actionRow.Controls.Add(renumberButton);
        actionRow.Controls.Add(refreshNameButton);
        actionRow.Controls.Add(exportCsvButton);
        actionRow.Controls.Add(generateDirectoryButton);
        actionRow.Controls.Add(splitDwgButton);
        actionRow.Controls.Add(MakeSeparator());
        actionRow.Controls.Add(openLogButton);
        settingsRow.Controls.Add(MakeLabel("输出:"), 0, 0);
        settingsRow.Controls.Add(_outputDirectory, 1, 0);
        settingsRow.Controls.Add(chooseOutputButton, 2, 0);
        settingsRow.Controls.Add(MakeLabel("打印机:"), 3, 0);
        settingsRow.Controls.Add(_deviceCombo, 4, 0);
        settingsRow.Controls.Add(MakeLabel("CTB:"), 5, 0);
        settingsRow.Controls.Add(_styleCombo, 6, 0);
        settingsRow.Controls.Add(_printButton, 7, 0);

        top.Controls.Add(actionRow, 0, 0);
        top.Controls.Add(settingsRow, 0, 1);
        pathRow.Controls.Add(new Label
        {
            Text = "保存路径快捷:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(8), UiLayout.Scale(8), 0)
        });
        pathRow.Controls.Add(_mergePdfCheckBox);
        pathRow.Controls.Add(_addSeqCheckBox);
        pathRow.Controls.Add(currentFolderButton);
        pathRow.Controls.Add(currentPdfButton);
        pathRow.Controls.Add(specifiedFolderButton);
        top.Controls.Add(pathRow, 0, 2);

        UiLayout.StyleGrid(_grid, Font);
        AddColumns();
        _grid.DataSource = _jobs;
        _grid.CellEndEdit += GridCellEndEdit;
        _grid.CellContentClick += GridCellContentClick;
        _grid.CellValueChanged += GridCellValueChanged;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
            {
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _grid.CellMouseDown += GridCellMouseDown;
        _grid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.RowIndex < _jobs.Count
                && _grid.Rows[e.RowIndex].DataBoundItem is PlotJob job)
            {
                // 换行只切换已有标注的高亮属性，避免整批删除重画导致 CAD 卡顿。
                HighlightSequenceOverlayJob(job);
            }
        };
        _grid.ContextMenuStrip = CreateGridContextMenu();

        _statusLabel.Dock = DockStyle.Bottom;
        _statusLabel.Height = Math.Max(UiLayout.Scale(28), Font.Height + UiLayout.Scale(10));
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);
        _statusLabel.BackColor = SystemColors.Control;

        Controls.Add(_grid);
        Controls.Add(_statusLabel);
        Controls.Add(top);

        void SetTip(Control control, string text)
        {
            tips.SetToolTip(control, text);
        }

        Control MakeSeparator()
        {
            return new Label
            {
                Width = UiLayout.Scale(1),
                Height = UiLayout.ButtonHeight() - UiLayout.Scale(8),
                BackColor = Color.FromArgb(205, 205, 205),
                Margin = new Padding(UiLayout.Scale(4), UiLayout.Scale(6), UiLayout.Scale(12), UiLayout.Scale(4))
            };
        }
    }

    private void AddColumns()
    {
        // 编号列（无绑定，CellFormatting 时显示行号）
        var indexCol = new DataGridViewTextBoxColumn { HeaderText = "编号", Width = UiLayout.Scale(52), ReadOnly = true };
        _grid.Columns.Add(indexCol);
        _grid.CellFormatting += (_, e) =>
        {
            if (e.ColumnIndex == indexCol.Index && e.RowIndex >= 0)
                e.Value = (e.RowIndex + 1).ToString();
        };

        _grid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "PreviewPdf",
            HeaderText = "预览",
            Text = "预览",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(64)
        });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(PlotJob.Selected), HeaderText = "打印", Width = UiLayout.Scale(58) });
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.OutputFileName), "PDF文件名", 220));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.DrawingNumber), "图号", 160, readOnly: false));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.Title), "图名", 240, readOnly: false));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.PaperName), "图幅", 82));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.ScaleText), "比例", 82));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.SizeText), "实际尺寸", 150));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.PaperSizeText), "输出纸张", 150));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.Date), "日期", 90));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.Revision), "版次", 70));
        _grid.Columns.Add(MakeTextColumn(nameof(PlotJob.Phase), "设计阶段", 90));

        static DataGridViewTextBoxColumn MakeTextColumn(string propertyName, string header, int width, bool readOnly = true)
        {
            return new DataGridViewTextBoxColumn
            {
                DataPropertyName = propertyName,
                HeaderText = header,
                Width = UiLayout.Scale(width),
                ReadOnly = readOnly
            };
        }
    }

    private void LoadPlotOptions()
    {
        try
        {
            AcadPlotterInstaller.InstallBundledPlotter();
            var settings = new PlotSettings(true);
            var validator = PlotSettingsValidator.Current;
            foreach (var deviceItem in validator.GetPlotDeviceList())
            {
                if (deviceItem is string device && !string.IsNullOrWhiteSpace(device))
                {
                    _deviceCombo.Items.Add(device);
                }
            }

            foreach (var styleItem in validator.GetPlotStyleSheetList())
            {
                if (styleItem is string style && style.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
                {
                    _styleCombo.Items.Add(style);
                }
            }

            SelectPlotDevice(_deviceCombo, _settings.LastPlotDevice);
            SelectExactOrContaining(_styleCombo, _settings.LastStyleSheet, "monochrome");
            if (_styleCombo.SelectedIndex < 0 && _styleCombo.Items.Count > 0)
            {
                _styleCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取打印设备/CTB失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void SelectPlotDevice(ComboBox combo, string lastValue)
    {
        if (TrySelectExactOrContaining(combo, AcadPlotterInstaller.PreferredPdfPlotter))
        {
            return;
        }

        if (TrySelectExactOrContaining(combo, lastValue))
        {
            return;
        }

        SelectExactOrContaining(combo, "", "PDF");
    }

    private static void SelectExactOrContaining(ComboBox combo, string exactValue, string fallbackContains)
    {
        if (TrySelectExactOrContaining(combo, exactValue))
        {
            return;
        }

        if (TrySelectContaining(combo, fallbackContains))
        {
            return;
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static bool TrySelectExactOrContaining(ComboBox combo, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return true;
            }
        }

        return TrySelectContaining(combo, value);
    }

    private static bool TrySelectContaining(ComboBox combo, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i]?.ToString()?.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                combo.SelectedIndex = i;
                return true;
            }
        }

        return false;
    }

    private TitleBlockScanScope? PromptScanScope() => BatchPlotCommands.PromptScanScope(this);

    private void ScanCurrentDrawing()
    {
        var library = TitleBlockLibraryStore.Load();
        if (library.Blocks.Count == 0)
        {
            MessageBox.Show("图框库为空。请先从“批量打印”菜单点击“新增图框”。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ClearSequenceOverlay();
            RefreshStatus();
            return;
        }

        var scope = PromptScanScope();
        if (scope == null)
        {
            return;
        }

        _jobs.Clear();
        var scannedJobs = TitleBlockScanner.Scan(_currentDocument, library, scope.Value);

        // 扫描结果坐标是 WCS，转为 DCS 后打印（和矩形框批量打印同理）
        TransformScannedJobsToDcs(scannedJobs);

        foreach (var job in scannedJobs)
        {
            _jobs.Add(job);
        }

        SortAndRefreshOutputPaths();
        ShowSequenceOverlayForCurrentJobs();
        AppendLog("INFO", $"扫描当前图完成，识别 {_jobs.Count} 张。");
    }

    private void ScanSelectedWindow()
    {
        var library = TitleBlockLibraryStore.Load();
        if (library.Blocks.Count == 0)
        {
            MessageBox.Show("图框库为空，请先新增图框。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            ClearSequenceOverlay();
            return;
        }

        Hide();
        System.Windows.Forms.Application.DoEvents();
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

            // 用户框选的是 UCS 坐标 → 四个角点转到 WCS 后取一次包围盒
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

            _jobs.Clear();
            var scannedJobs = TitleBlockScanner.Scan(_currentDocument, library, window);

            // 扫描结果坐标是 WCS，转为 DCS 后打印
            TransformScannedJobsToDcs(scannedJobs);

            foreach (var job in scannedJobs)
            {
                _jobs.Add(job);
            }

            SortAndRefreshOutputPaths();
            ShowSequenceOverlayForCurrentJobs();
            AppendLog("INFO", $"框选扫描当前图完成，识别 {_jobs.Count} 张。");
        }
        finally
        {
            Show();
            Activate();
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
        using var dialog = new OpenFileDialog
        {
            Filter = "DWG 文件 (*.dwg)|*.dwg",
            Multiselect = true,
            Title = "选择需要批量打印的 DWG"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
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

        foreach (var job in added)
        {
            _jobs.Add(job);
        }

        SortAndRefreshOutputPaths();
        if (_jobs.Count > 0 && _jobs.All(IsCurrentDocumentJob))
        {
            ShowSequenceOverlayForCurrentJobs();
        }
        else
        {
            ClearSequenceOverlay();
        }

        if (errors.Count > 0)
        {
            MessageBox.Show("部分 DWG 扫描失败:\n" + string.Join("\n", errors), "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private List<PlotJob> ScanExternalFile(string file, TitleBlockLibrary library)
    {
        if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(_currentDocument.Database.Filename), StringComparison.OrdinalIgnoreCase))
        {
            return TitleBlockScanner.Scan(_currentDocument, library, TitleBlockScanScope.AllSpaces);
        }

        using var db = new Database(false, true);
        db.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, true, "");
        db.CloseInput(true);
        return TitleBlockScanner.Scan(db, library, file, null, TitleBlockScanScope.AllSpaces);
    }

    private void SortAndRefreshOutputPaths()
    {
        var sorted = _jobs
            .OrderByDescending(x => x.SortPriority)
            .ThenBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _jobs.Clear();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var idx = 0;
        foreach (var job in sorted)
        {
            var seq = _addSeqCheckBox.Checked ? $"{++idx}{_settings.PdfFileNameSeparator}" : "";
            job.OutputPath = BuildOutputPath(job, seq, reservedPaths);
            _jobs.Add(job);
        }

        RefreshStatus();
        if (_sequenceOverlayFollowsCurrentJobs)
        {
            ShowSequenceOverlayForCurrentJobs();
        }
    }

    private ContextMenuStrip CreateGridContextMenu()
    {
        var menu = new ContextMenuStrip();
        var moveToFirst = new ToolStripMenuItem("移到第一个");
        moveToFirst.Click += (_, _) => MoveCurrentJobToFirst();
        var markNotPrint = new ToolStripMenuItem("不打印");
        markNotPrint.Click += (_, _) => MarkHighlightedJobsNotPrint();
        var delete = new ToolStripMenuItem("删除");
        delete.Click += (_, _) => RemoveHighlightedJobs();
        menu.Items.Add(moveToFirst);
        menu.Items.Add(markNotPrint);
        menu.Items.Add(delete);
        menu.Opening += (_, e) =>
        {
            var enabled = _grid.CurrentRow?.DataBoundItem is PlotJob;
            moveToFirst.Enabled = enabled;
            markNotPrint.Enabled = enabled;
            delete.Enabled = enabled;
            e.Cancel = !enabled;
        };
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

    private void MoveCurrentJobToFirst()
    {
        if (_grid.CurrentRow?.DataBoundItem is not PlotJob job)
        {
            return;
        }

        job.SortPriority = ++_nextSortPriority;
        SortAndRefreshOutputPaths();
        var row = _grid.Rows
            .Cast<DataGridViewRow>()
            .FirstOrDefault(x => ReferenceEquals(x.DataBoundItem, job));
        if (row != null)
        {
            row.Selected = true;
            _grid.CurrentCell = row.Cells[0];
        }
    }

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

    private void ClearSequenceOverlay()
    {
        _sequenceOverlayFollowsCurrentJobs = false;
        _sequenceOverlay.Clear();
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

    private void SetAllSelected(bool selected)
    {
        _grid.EndEdit();
        foreach (var job in _jobs)
        {
            job.Selected = selected;
        }

        _grid.Refresh();
        RefreshStatus();
        RefreshSelectedOverlay();
    }

    private void InvertSelected()
    {
        _grid.EndEdit();
        foreach (var job in _jobs)
        {
            job.Selected = !job.Selected;
        }

        _grid.Refresh();
        RefreshStatus();
        RefreshSelectedOverlay();
    }

    private void MarkHighlightedJobsNotPrint()
    {
        foreach (var job in GetHighlightedJobs())
        {
            job.Selected = false;
        }

        _grid.Refresh();
        RefreshStatus();
        RefreshSelectedOverlay();
    }

    private List<PlotJob> GetHighlightedJobs()
    {
        var jobs = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<PlotJob>()
            .Distinct()
            .ToList();
        if (jobs.Count == 0 && _grid.CurrentRow?.DataBoundItem is PlotJob current)
        {
            jobs.Add(current);
        }

        return jobs;
    }

    private void RemoveHighlightedJobs()
    {
        _grid.EndEdit();
        var highlightedJobs = GetHighlightedJobs();

        if (highlightedJobs.Count == 0)
        {
            MessageBox.Show("没有高亮选中的图纸行。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        foreach (var job in highlightedJobs)
        {
            _jobs.Remove(job);
        }

        SortAndRefreshOutputPaths();
        RefreshSelectedOverlay();
    }

    private void GridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0
            || _grid.Columns[e.ColumnIndex].DataPropertyName != nameof(PlotJob.Selected))
        {
            return;
        }

        RefreshStatus();
        RefreshSelectedOverlay();
    }

    private void RefreshSelectedOverlay()
    {
        if (_sequenceOverlayFollowsCurrentJobs)
        {
            ShowSequenceOverlayForCurrentJobs();
        }
    }

    private void RenumberDrawingNumbers()
    {
        if (_jobs.Count == 0) return;

        // 仅对当前文档的图框重排
        var currentJobs = _jobs.Where(j => IsCurrentDocumentJob(j)).ToList();
        if (currentJobs.Count == 0)
        {
            MessageBox.Show("当前没有本图文档的图框可重排。", "图号重排", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 保存原始图号，取消时恢复
        var originalNumbers = currentJobs.ToDictionary(j => j, j => j.DrawingNumber);

        using var dialog = new DrawingNumberReorderDialog(currentJobs.Count);
        dialog.PreviewRequested += () =>
        {
            var sorted = SortSpatially(currentJobs, dialog.HorizontalFirst);
            ApplyRenumbering(sorted, dialog.Prefix, dialog.StartNumber);
            _grid.Refresh();
            ShowSequenceOverlayForCurrentJobs();
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            // 恢复原始图号
            foreach (var kv in originalNumbers)
            {
                kv.Key.DrawingNumber = kv.Value;
            }
            _grid.Refresh();
            SortAndRefreshOutputPaths();
            ShowSequenceOverlayForCurrentJobs();
            return;
        }

        var finalSorted = SortSpatially(currentJobs, dialog.HorizontalFirst);
        ApplyRenumbering(finalSorted, dialog.Prefix, dialog.StartNumber);
        _grid.Refresh();
        SortAndRefreshOutputPaths();
        ShowSequenceOverlayForCurrentJobs();

        // 反写 CAD 文件中的图号
        var updated = 0;
        foreach (var job in finalSorted)
        {
            if (CadTextUpdater.TryUpdateOpenDocument(job, null, job.DrawingNumber, _currentDocument, out _))
            {
                updated++;
            }
        }

        AppendLog("INFO", $"图号重排完成，{finalSorted.Count} 张图框按" + (dialog.HorizontalFirst ? "从左到右、从上到下" : "从上到下、从左到右") + $"排序，已反写 CAD {updated} 处。");
    }

    private static void ApplyRenumbering(IReadOnlyList<PlotJob> sorted, string prefix, int start)
    {
        var digits = Math.Max(2, (sorted.Count + start - 1).ToString().Length);
        for (var i = 0; i < sorted.Count; i++)
        {
            sorted[i].DrawingNumber = prefix + (start + i).ToString($"D{digits}");
            sorted[i].CadDrawingNumber = sorted[i].DrawingNumber;
        }
    }

    private static List<PlotJob> SortSpatially(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        if (jobs.Count <= 1) return jobs.ToList();

        var centers = horizontalFirst
            ? jobs.Select(CenterX).Distinct().OrderBy(x => x).ToList()
            : jobs.Select(CenterY).Distinct().OrderBy(y => y).ToList();
        var gaps = centers.Zip(centers.Skip(1), (a, b) => Math.Abs(b - a))
            .Where(g => g > 1e-6).OrderBy(g => g).ToList();
        var medianGap = gaps.Count > 0 ? gaps[gaps.Count / 2] : 1.0;
        var bandTolerance = Math.Max(medianGap * 0.5, 1e-6);

        var remaining = horizontalFirst
            ? jobs.OrderBy(CenterX).ToList()
            : jobs.OrderByDescending(CenterY).ToList();
        var result = new List<PlotJob>();

        while (remaining.Count > 0)
        {
            var anchor = horizontalFirst ? CenterX(remaining[0]) : CenterY(remaining[0]);
            var band = remaining
                .Where(j => Math.Abs((horizontalFirst ? CenterX(j) : CenterY(j)) - anchor) <= bandTolerance)
                .ToList();
            foreach (var j in band) remaining.Remove(j);
            result.AddRange(horizontalFirst
                ? band.OrderByDescending(CenterY)
                : band.OrderBy(CenterX));
        }

        return result;
    }

    private static double CenterX(PlotJob job) => (job.MinX + job.MaxX) / 2d;
    private static double CenterY(PlotJob job) => (job.MinY + job.MaxY) / 2d;

    private void ClearJobs()
    {
        _grid.EndEdit();
        if (_jobs.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("确定清空当前图纸清单吗？", "批量打印", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _jobs.Clear();
        _selectedDwgFiles.Clear();
        ClearSequenceOverlay();
        RefreshStatus();
    }

    private string BuildOutputPath(PlotJob job, string seqPrefix, ISet<string> reservedPaths)
    {
        var fields = _settings.PdfFileNameFields;
        if (fields == null || fields.Count == 0)
        {
            fields = new System.Collections.Generic.List<string> { "DrawingNumber", "Title" };
        }

        var parts = GetFileNameParts(job, fields);
        if (parts.Count == 0)
        {
            parts.Add(job.DrawingNumber); // fallback
        }

        var separator = _settings.PdfFileNameSeparator;
        var baseName = seqPrefix + string.Join(separator, parts);
        return FileNameSanitizer.MakeUnique(_outputDirectory.Text, baseName, reservedPaths, _settings.AddSequenceWhenPdfExists);
    }

    private static List<string> GetFileNameParts(PlotJob job, List<string> fieldKeys)
    {
        var parts = new List<string>();
        foreach (var key in fieldKeys)
        {
            var value = key switch
            {
                "DrawingNumber" => job.DrawingNumber,
                "Title" => job.Title,
                "Date" => job.Date,
                "Revision" => job.Revision,
                "Phase" => job.Phase,
                "PaperName" => job.PaperName,
                _ => ""
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value.Trim());
            }
        }

        return parts;
    }

    private string GetDefaultOutputDirectory()
    {
        if (_settings.RememberLastOutputDirectory && !string.IsNullOrWhiteSpace(_settings.LastOutputDirectory))
        {
            return _settings.LastOutputDirectory;
        }

        var file = _currentDocument.Database.Filename;
        var directory = string.IsNullOrWhiteSpace(file)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : Path.GetDirectoryName(file) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        return Path.Combine(directory, _settings.DefaultOutputSubfolder);
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
            _outputDirectory.Text = dialog.SelectedPath;
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

    private void SetOutputDirectory(string directory)
    {
        _outputDirectory.Text = directory;
        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "输出目录切换为 " + directory);
    }

    private void ExportCsv()
    {
        if (_jobs.Count == 0)
        {
            MessageBox.Show("当前没有可导出的图纸清单。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV 清单 (*.csv)|*.csv",
            FileName = "批量打印清单.csv",
            Title = "导出批量打印清单"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        CsvExporter.ExportJobs(dialog.FileName, _jobs);
        AppendLog("INFO", "导出清单 " + dialog.FileName);
        MessageBox.Show("清单已导出。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GenerateDrawingDirectory()
    {
        if (_jobs.Count == 0)
        {
            MessageBox.Show("当前没有可生成目录的图纸清单。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Hide();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            var ok = DirectoryTableGenerator.PromptAndGenerate(_currentDocument, _jobs.ToList(), _settings, out var message);
            AppendLog(ok ? "INFO" : "WARN", message);
            MessageBox.Show(message, "批量打印", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void SplitSelectedDwgs()
    {
        _grid.EndEdit();
        var selectedJobs = _jobs.Where(x => x.Selected).ToList();
        if (selectedJobs.Count == 0)
        {
            MessageBox.Show("请先勾选需要拆图的图纸。", "批量拆图", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"将按当前勾选清单拆出 {selectedJobs.Count} 个 DWG 文件。\n\n模型空间: 新建轻量 DWG，只复制图框范围内或相交对象，打开后自动居中显示。\n布局空间: 不动模型空间，只保留当前布局并清理布局内其他图素。\n\n输出位置: 每个源 DWG 所在目录下的 DWG 文件夹。\n\n是否继续？",
            "批量拆图",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        Cursor = Cursors.WaitCursor;
        Enabled = false;
        try
        {
            AppendLog("INFO", $"开始批量拆图，共 {selectedJobs.Count} 张。");
            var results = DwgSplitService.SplitMany(
                selectedJobs,
                _currentDocument,
                _settings,
                job => AppendLog("INFO", $"开始拆图 {job.DrawingNumber}_{job.Title}"));

            var success = results.Count(x => x.Error == null);
            var failed = results.Count - success;
            foreach (var result in results)
            {
                if (result.Error == null)
                {
                    var actionText = result.Job.IsPaperSpace ? "清理" : "跳过";
                    AppendLog("INFO", $"拆图成功 {result.OutputPath}，保留 {result.KeptEntities} 个对象，{actionText} {result.RemovedEntities} 个对象，未知外包框保留 {result.UnknownExtentsKept} 个。");
                }
                else
                {
                    AppendLog("ERROR", $"拆图失败 {result.Job.DrawingNumber}_{result.Job.Title}: {result.Error.Message}");
                }
            }

            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            RefreshStatus();

            var failedText = failed == 0
                ? ""
                : "\n\n失败项:\n" + string.Join("\n", results.Where(x => x.Error != null).Take(20).Select(x => $"{x.Job.DrawingNumber}_{x.Job.Title}: {x.Error!.Message}"));
            MessageBox.Show(
                $"拆图完成: 成功 {success} 张，失败 {failed} 张。\n日志:\n{_lastLogPath}{failedText}",
                "批量拆图",
                MessageBoxButtons.OK,
                failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private void ManageLibrary()
    {
        using var form = new TitleBlockLibraryManagerForm();
        form.ShowDialog(this);
        if (form.LibraryChanged)
        {
            ScanCurrentDrawing();
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_currentDocument);
        if (form.ShowDialog(this) == DialogResult.OK)
        {
            ReloadSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "设置已更新。");

            if (form.RequestPickDirectoryCellSizes)
            {
                PickDirectoryCellSizesFromCad();
            }
        }
    }

    private void PickDirectoryCellSizesFromCad()
    {
        Hide();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            var ok = DirectoryTableGenerator.PromptCellSizes(_currentDocument, _settings, out _, out var message);
            ReloadSettings();
            AppendLog(ok ? "INFO" : "WARN", message);
            MessageBox.Show(message, "批量打印设置", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void ReloadSettings()
    {
        var updated = AppSettingsStore.Load();
        _settings.RememberLastOutputDirectory = updated.RememberLastOutputDirectory;
        _settings.DefaultOutputSubfolder = updated.DefaultOutputSubfolder;
        _settings.AutoScanCurrentDrawing = updated.AutoScanCurrentDrawing;
        _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
        _settings.AllowStandardPaperNameFallback = updated.AllowStandardPaperNameFallback;
        _settings.ShowPlotProgress = updated.ShowPlotProgress;
        _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
        _settings.PdfFileNameSeparator = updated.PdfFileNameSeparator;
        _settings.OpenExternalDwgForPlot = updated.OpenExternalDwgForPlot;
        _settings.DirectoryIndexWidth = updated.DirectoryIndexWidth;
        _settings.DirectoryNumberWidth = updated.DirectoryNumberWidth;
        _settings.DirectoryTitleWidth = updated.DirectoryTitleWidth;
        _settings.DirectoryPaperWidth = updated.DirectoryPaperWidth;
        _settings.DirectoryRemarkWidth = updated.DirectoryRemarkWidth;
        _settings.DirectoryRowHeight = updated.DirectoryRowHeight;
        _settings.DirectoryTextHeightRatio = updated.DirectoryTextHeightRatio;
        _settings.DirectoryTextStyleName = updated.DirectoryTextStyleName;
    }

    private void OpenLastLog()
    {
        if (string.IsNullOrWhiteSpace(_lastLogPath) || !File.Exists(_lastLogPath))
        {
            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
        }

        Process.Start(_lastLogPath);
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
        MessageBox.Show("图框库已导入。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        MessageBox.Show("图框库已导出。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void PrintSelectedJobs()
    {
        var selected = _jobs.Where(x => x.Selected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("没有勾选任何图纸。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var device = _deviceCombo.SelectedItem?.ToString() ?? "";
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show("请选择 PDF 打印机。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _mergedOutputPath = "";
        if (_mergePdfCheckBox.Checked)
        {
            using var mergeDialog = new SaveFileDialog
            {
                Filter = "PDF 文件 (*.pdf)|*.pdf",
                InitialDirectory = Directory.Exists(_outputDirectory.Text) ? _outputDirectory.Text : "",
                FileName = GetDefaultMergedFileName(),
                Title = "保存合并 PDF"
            };
            if (mergeDialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            _mergedOutputPath = mergeDialog.FileName;
        }

        Directory.CreateDirectory(_outputDirectory.Text);
        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        HasPendingPrint = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    public void ExecutePendingPrint()
    {
        if (!HasPendingPrint)
        {
            return;
        }

        var selected = _jobs.Where(x => x.Selected).ToList();
        var device = _deviceCombo.SelectedItem?.ToString() ?? "";
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        var mergePdf = !string.IsNullOrWhiteSpace(_mergedOutputPath);
        var originalOutputPaths = selected.ToDictionary(job => job, job => job.OutputPath);
        string? temporaryDirectory = null;
        var mergedSuccessfully = false;

        ShowSequenceOverlayForPrint(selected);
        _printButton.Enabled = false;
        var wasVisible = Visible;
        if (_settings.OpenExternalDwgForPlot)
        {
            Hide();
            System.Windows.Forms.Application.DoEvents();
        }

        try
        {
            var failed = new List<string>();
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

            var results = PlotterService.PlotMany(
                selected,
                device,
                style,
                _currentDocument,
                _settings,
                job => AppendLog("INFO", $"开始打印 {job.DrawingNumber}_{job.Title} -> {job.OutputPath}"));

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
                    PdfDocumentService.Merge(selected.Select(job => job.OutputPath).ToList(), _mergedOutputPath);
                    mergedSuccessfully = true;
                    AppendLog("INFO", $"合并 PDF 成功 {_mergedOutputPath}");
                }
                catch (Exception ex)
                {
                    var message = "合并 PDF 失败: " + ex.Message;
                    failed.Add(message);
                    AppendLog("ERROR", ex.ToString());
                }
            }

            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            var summary = mergePdf
                ? mergedSuccessfully
                    ? $"打印并合并完成: 共 {printed} 张。\n合并文件: {_mergedOutputPath}\n日志: {_lastLogPath}"
                    : $"打印或合并失败，未生成新的合并 PDF。\n成功打印 {printed} 张，失败 {failed.Count} 项。\n日志: {_lastLogPath}"
                : $"打印完成: 成功 {printed} 张，失败 {failed.Count} 张。\n日志: {_lastLogPath}";
            if (failed.Count > 0)
            {
                summary += "\n\n失败项:\n" + string.Join("\n", failed);
            }

            MessageBox.Show(summary, "批量打印", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

            if ((!mergePdf && printed > 0) || mergedSuccessfully)
            {
                OpenOutputDirectoryAfterPrint(mergePdf ? _mergedOutputPath : null);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("打印失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            if (wasVisible && !Visible)
            {
                Show();
                Activate();
            }

            _printButton.Enabled = true;
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

    private void GridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0
            || e.ColumnIndex < 0
            || !string.Equals(_grid.Columns[e.ColumnIndex].Name, "PreviewPdf", StringComparison.Ordinal))
        {
            return;
        }

        if (_grid.Rows[e.RowIndex].DataBoundItem is PlotJob job)
        {
            PreviewJob(job);
        }
    }

    private void PreviewJob(PlotJob job)
    {
        var device = _deviceCombo.SelectedItem?.ToString() ?? "";
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show("请选择打印机。", "打印预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var wasVisible = Visible;
        try
        {
            Hide();
            System.Windows.Forms.Application.DoEvents();
            AppendLog("INFO", $"CAD 内部预览 {job.DrawingNumber}_{job.Title}");
            PlotterService.Preview(job, device, style, _currentDocument);
        }
        catch (Exception ex)
        {
            AppendLog("ERROR", "打印预览失败: " + ex);
            MessageBox.Show("打印预览失败: " + ex.Message, "打印预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && !Visible)
            {
                Show();
                Activate();
            }
        }
    }

    private string GetDefaultMergedFileName()
    {
        var source = _jobs
            .Where(job => job.Selected)
            .Select(job => job.SourceFile)
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var baseName = string.IsNullOrWhiteSpace(source)
            ? "合并图纸"
            : Path.GetFileNameWithoutExtension(source);
        return FileNameSanitizer.Clean(baseName) + "_合并.pdf";
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

    private void GridCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        if (_grid.Rows[e.RowIndex].DataBoundItem is not PlotJob job)
        {
            return;
        }

        var property = _grid.Columns[e.ColumnIndex].DataPropertyName;
        var titleChanged = property == nameof(PlotJob.Title) && !string.Equals(job.Title, job.CadTitle, StringComparison.Ordinal);
        var numberChanged = property == nameof(PlotJob.DrawingNumber) && !string.Equals(job.DrawingNumber, job.CadDrawingNumber, StringComparison.Ordinal);
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
        SortAndRefreshOutputPaths();
    }

    private void AppendLog(string level, string message)
    {
        _logLines.Add(BatchPlotLogger.Format(level, message));
    }

    private void RefreshStatus()
    {
        var selected = _jobs.Count(x => x.Selected);
        _statusLabel.Text = $"共 {_jobs.Count} 张，已勾选 {selected} 张。图框库: {TitleBlockLibraryStore.DefaultPath}";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!HasPendingPrint)
        {
            ClearSequenceOverlay();
        }

        SaveCurrentSettings();
        base.OnFormClosing(e);
    }

    private void SaveCurrentSettings()
    {
        _settings.LastOutputDirectory = _outputDirectory.Text;
        _settings.LastPlotDevice = _deviceCombo.SelectedItem?.ToString() ?? "";
        _settings.LastStyleSheet = _styleCombo.SelectedItem?.ToString() ?? "";
        _settings.AutoScanCurrentDrawing = false;
        AppSettingsStore.Save(_settings);
    }
}
