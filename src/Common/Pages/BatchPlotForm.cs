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
    private readonly ComboBox _outputFormatCombo = new();
    private readonly ComboBox _savePathModeCombo = new();
    private readonly ComboBox _styleCombo = new();
    private readonly CheckBox _mergePdfCheckBox = new();
    private readonly CheckBox _leaveMarginCheckBox = new();
    private readonly ComboBox _marginInput = new();
    private readonly Button _printButton = new();
    private readonly List<Control> _plotOnlyControls = new();
    private CancellationTokenSource? _printCts;
    private readonly Label _statusLabel = new();
    private readonly List<string> _logLines = new();
    private readonly List<string> _selectedDwgFiles = new();
    private readonly TemporarySequenceOverlay _sequenceOverlay;
    private readonly AppSettings _settings;
    private bool _sequenceOverlayFollowsCurrentJobs;
    private bool _outputDirectoryIsCustom;
    private bool _updatingPrintSelection;
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
    public bool HasPendingPrint { get; private set; }

    public BatchPlotForm(Document currentDocument)
    {
        _currentDocument = currentDocument;
        _sequenceOverlay = new TemporarySequenceOverlay(currentDocument);
        // 订阅红框删除事件：在 CAD 中 ERASE 红框即可同步删除表格中对应的打印任务
        _sequenceOverlay.FrameErased += SequenceOverlayFrameErased;
        _settings = AppSettingsStore.Load();
        InitializeComponents();
        LoadPlotOptions();
        RefreshStatus();
    }

    private void InitializeComponents()
    {
#if AUTOCAD
        Text = "LA图框块批量打印";
#else
        Text = "LA图框块批量打印";
#endif
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ClientSize = new Size(UiLayout.Scale(900), UiLayout.Scale(520));
        StartPosition = FormStartPosition.CenterScreen;
        Font = UiLayout.DefaultFont;
        var tips = new ToolTip
        {
            AutoPopDelay = 8000,
            InitialDelay = 450,
            ReshowDelay = 100,
            ShowAlways = true
        };

        var actionRowHeight = UiLayout.ButtonHeight() + UiLayout.Scale(7);
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = actionRowHeight + UiLayout.ButtonHeight() * 2 + UiLayout.Scale(30),
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(6)),
            BackColor = SystemColors.Control
        };
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, actionRowHeight));
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
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(74)));
        settingsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(116)));
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

        var clearButton = MakeButton("清空清单", 92);
        clearButton.Click += (_, _) => ClearJobs();

        var renumberButton = MakeButton("图号重排", 92);
        renumberButton.Click += (_, _) => RenumberDrawingNumbers();

        var refreshNameButton = MakeButton("刷新文件名", 104);
        refreshNameButton.Click += (_, _) => SortAndRefreshOutputPaths();

        var generateDirectoryButton = MakeButton("生成目录", 92);
        generateDirectoryButton.Click += (_, _) => GenerateDrawingDirectory();

        var chooseOutputButton = MakeButton("浏览...", 84);
        chooseOutputButton.Click += (_, _) => ChooseOutputDirectory();

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
        _printButton.Click += (_, _) => PrintOrStop();
        tips.SetToolTip(_printButton, "按输出格式打印 PDF/DWF、输出 PNG/JPG 或拆分为 DWG。");

        SetTip(scanButton, "扫描当前打开图纸中的全部匹配图框。");
        SetTip(scanWindowButton, "回到 CAD 框选区域，只识别框内图框。");
        SetTip(addFilesButton, "选择一个或多个 DWG，加入批量打印清单。");
        SetTip(clearButton, "清空当前清单，不影响 CAD 文件和图框库。");
        SetTip(renumberButton, "按空间位置重新排序图框，按顺序分配前缀+递增图号。");
        SetTip(refreshNameButton, "按当前图号、图名和设置重新生成输出文件名。");
        SetTip(generateDirectoryButton, "在当前 CAD 指定基点，生成图纸目录表。");
        SetTip(chooseOutputButton, "手动选择输出目录。");

        _outputDirectory.Dock = DockStyle.Fill;
        _outputDirectory.Margin = new Padding(0, UiLayout.Scale(4), UiLayout.Scale(8), UiLayout.Scale(8));
        _outputDirectory.Text = GetDefaultOutputDirectory();
        _outputDirectory.Leave += (_, _) => ApplyManuallyEnteredOutputDirectory();

        _outputFormatCombo.Dock = DockStyle.Fill;
        _outputFormatCombo.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(10), UiLayout.Scale(8));
        _outputFormatCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _outputFormatCombo.SelectionChangeCommitted += (_, _) => UpdateOutputFormatUi();

        _savePathModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _savePathModeCombo.Width = UiLayout.Scale(210);
        _savePathModeCombo.Margin = new Padding(0, UiLayout.Scale(3), UiLayout.Scale(8), UiLayout.Scale(3));
        _savePathModeCombo.SelectionChangeCommitted += (_, _) => ApplySelectedSavePathMode();

        _styleCombo.Dock = DockStyle.Fill;
        _styleCombo.Margin = new Padding(0, UiLayout.Scale(3), 0, UiLayout.Scale(8));
        _styleCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        // 用户手动切换 CTB 后立即保存，保证其它打印入口下次默认沿用上一次选择。
        _styleCombo.SelectionChangeCommitted += (_, _) => SaveCurrentSettings();

        _mergePdfCheckBox.Text = "合并 PDF";
        _mergePdfCheckBox.AutoSize = true;
        // 首次使用默认为关闭；之后恢复用户上一次操作，避免每次重复勾选。
        _mergePdfCheckBox.Checked = _settings.MergePdf;
        _mergePdfCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _mergePdfCheckBox.Margin = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), 0);
        SetTip(_mergePdfCheckBox, "勾选后合并临时单页；可在批量打印设置中启用文件名书签或按纸张大小分别合并。");

        _leaveMarginCheckBox.Text = "周边留白";
        _leaveMarginCheckBox.AutoSize = true;
        _leaveMarginCheckBox.Checked = _settings.LeavePaperMargin;
        _leaveMarginCheckBox.TextAlign = ContentAlignment.MiddleLeft;
        _leaveMarginCheckBox.Margin = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), 0);
        SetTip(_leaveMarginCheckBox, "勾选后按设定距离留白。正数=扩大纸张（比例不变）；负数=缩小比例。");
        InitMarginCombo(_marginInput, UiLayout.Scale(68), _settings.PaperMarginMm);
        _marginInput.Enabled = _leaveMarginCheckBox.Checked;
        _marginInput.Margin = new Padding(0, UiLayout.Scale(4), 0, 0);
        _leaveMarginCheckBox.CheckedChanged += (_, _) => _marginInput.Enabled = _leaveMarginCheckBox.Checked;

        actionRow.Controls.Add(scanButton);
        actionRow.Controls.Add(scanWindowButton);
        actionRow.Controls.Add(addFilesButton);
        actionRow.Controls.Add(MakeSeparator());
        actionRow.Controls.Add(clearButton);
        actionRow.Controls.Add(MakeSeparator());
        actionRow.Controls.Add(renumberButton);
        actionRow.Controls.Add(refreshNameButton);
        actionRow.Controls.Add(generateDirectoryButton);
        settingsRow.Controls.Add(MakeLabel("输出:"), 0, 0);
        settingsRow.Controls.Add(_outputDirectory, 1, 0);
        settingsRow.Controls.Add(chooseOutputButton, 2, 0);
        settingsRow.Controls.Add(MakeLabel("输出格式:"), 3, 0);
        settingsRow.Controls.Add(_outputFormatCombo, 4, 0);
        settingsRow.Controls.Add(MakeLabel("CTB:"), 5, 0);
        settingsRow.Controls.Add(_styleCombo, 6, 0);
        settingsRow.Controls.Add(_printButton, 7, 0);

        top.Controls.Add(actionRow, 0, 0);
        top.Controls.Add(settingsRow, 0, 1);
        var savePathLabel = new Label
        {
            Text = "保存路径:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(8), UiLayout.Scale(8), 0)
        };
        pathRow.Controls.Add(savePathLabel);
        pathRow.Controls.Add(_savePathModeCombo);
        pathRow.Controls.Add(_mergePdfCheckBox);
        pathRow.Controls.Add(_leaveMarginCheckBox);
        pathRow.Controls.Add(_marginInput);
        pathRow.Controls.Add(new Label { Text = "mm", AutoSize = true, Margin = new Padding(3, UiLayout.Scale(8), UiLayout.Scale(8), 0) });
        top.Controls.Add(pathRow, 0, 2);

        _plotOnlyControls.AddRange(new Control[]
        {
            _styleCombo,
            _leaveMarginCheckBox,
            _marginInput
        });

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

        // ── 底部快捷设置栏 ──
        var quickBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = UiLayout.ButtonHeight() + UiLayout.Scale(8),
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(UiLayout.Scale(8), UiLayout.Scale(3), 0, UiLayout.Scale(3)),
            BackColor = SystemColors.Control
        };
        var sortSettingsButton = MakeButton("排序设置", 80);
        var fileNameSettingsButton = MakeButton("文件名设置", 90);
        var directorySettingsButton = MakeButton("目录设置", 80);
        sortSettingsButton.Margin = new Padding(0, 0, UiLayout.Scale(4), 0);
        fileNameSettingsButton.Margin = new Padding(0, 0, UiLayout.Scale(4), 0);
        directorySettingsButton.Margin = Padding.Empty;
        sortSettingsButton.Click += (_, _) => ShowSortSettings();
        fileNameSettingsButton.Click += (_, _) => ShowSettingsAtTab(1);
        directorySettingsButton.Click += (_, _) => ShowSettingsAtTab(2);
        tips.SetToolTip(sortSettingsButton, "选择图框空间排列顺序，并按选定方向重排当前清单。");
        tips.SetToolTip(fileNameSettingsButton, "配置输出文件名格式、序号位数等，直接跳转到文件名标签页。");
        tips.SetToolTip(directorySettingsButton, "配置图纸目录列宽、字高等，直接跳转到目录标签页。");
        quickBar.Controls.Add(sortSettingsButton);
        quickBar.Controls.Add(fileNameSettingsButton);
        quickBar.Controls.Add(directorySettingsButton);
        Controls.Add(quickBar);

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
            _pngPlotDevice = FindPngPlotDevice(devices, installedPngPlotter);
            _jpgPlotDevice = FindJpgPlotDevice(devices, installedJpgPlotter);
            _dwfPlotDevice = FindDwfPlotDevice(devices, installedDwfPlotter);
            foreach (var styleItem in validator.GetPlotStyleSheetList())
            {
                if (styleItem is string style && style.EndsWith(".ctb", StringComparison.OrdinalIgnoreCase))
                {
                    _styleCombo.Items.Add(style);
                }
            }

            SelectExactOrContaining(_styleCombo, _settings.LastStyleSheet, "monochrome");
            if (_styleCombo.SelectedIndex < 0 && _styleCombo.Items.Count > 0)
            {
                _styleCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("读取 CTB 失败: " + ex.Message, "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateOutputFormatUi();
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
        _selectedDwgFiles.Clear();
        var scannedJobs = TitleBlockScanner.Scan(
            _currentDocument,
            library,
            scope.Value,
            _settings.PaperMatchToleranceMm);

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
            _selectedDwgFiles.Clear();
            var scannedJobs = TitleBlockScanner.Scan(
                _currentDocument,
                library,
                window,
                _settings.PaperMatchToleranceMm);

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

    private void SortAndRefreshOutputPaths()
    {
        if (!_outputDirectoryIsCustom)
        {
            UpdateAutomaticOutputDirectory();
        }

        var sorted = _jobs
            .OrderByDescending(x => x.SortPriority)
            .ThenBy(x => x.DrawingNumber, NaturalStringComparer.Instance)
            .ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _jobs.Clear();
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
        if (e.RowIndex < 0)
        {
            return;
        }

        if (e.Button == MouseButtons.Left
            && e.ColumnIndex >= 0
            && _grid.Columns[e.ColumnIndex].DataPropertyName == nameof(PlotJob.Selected)
            && _grid.SelectedRows.Count > 1
            && _grid.Rows[e.RowIndex].DataBoundItem is PlotJob clickedJob)
        {
            // 先记住点击前的多选行；DataGrid 点击复选框时可能会先改当前选择，后续 CellValueChanged 再统一同步这些行。
            var highlightedJobs = GetHighlightedJobs();
            _pendingPrintToggleJobs = highlightedJobs.Contains(clickedJob) ? highlightedJobs : null;
        }

        if (e.Button != MouseButtons.Right)
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

    // CAD 中红框被 ERASE 删除后的回调：同步从任务列表中移除对应打印任务
    private void SequenceOverlayFrameErased(PlotJob job)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        void RemoveJob()
        {
            if (!_jobs.Contains(job))
            {
                return;
            }

            if (ReferenceEquals(_highlightedJob, job))
            {
                _highlightedJob = null;
            }

            _jobs.Remove(job);
            // 移除后重新排序、编号并刷新覆盖层，保持表格与 CAD 红框一致
            SortAndRefreshOutputPaths();
        }

        // CAD 事件可能来自非 UI 线程，需切回窗体线程再操作绑定列表
        if (InvokeRequired)
        {
            BeginInvoke((Action)RemoveJob);
        }
        else
        {
            RemoveJob();
        }
    }

    private void GridCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingPrintSelection
            || e.RowIndex < 0 || e.ColumnIndex < 0
            || _grid.Columns[e.ColumnIndex].DataPropertyName != nameof(PlotJob.Selected))
        {
            return;
        }

        if (_grid.Rows[e.RowIndex].DataBoundItem is PlotJob changedJob)
        {
            ApplyPrintSelectionToHighlightedRows(changedJob);
        }

        RefreshStatus();
        RefreshSelectedOverlay();
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

        _grid.Refresh();
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
        if (_renumberDialog != null)
        {
            _renumberDialog.Activate();
            return;
        }

        // 仅对当前文档的图框重排
        var currentJobs = _jobs.Where(j => IsCurrentDocumentJob(j)).ToList();
        if (currentJobs.Count == 0)
        {
            MessageBox.Show("当前没有本图文档的图框可重排。", "图号重排", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // 保存原始图号，非模态窗口取消/关闭时恢复。
        _renumberCurrentJobs = currentJobs;
        _renumberOriginalNumbers = currentJobs.ToDictionary(j => j, j => j.DrawingNumber);
        var detectedPrefix = DetectCommonDrawingNumberPrefix(currentJobs);
        _renumberDialog = new DrawingNumberReorderDialog(currentJobs.Count, detectedPrefix);
        _renumberDialog.PreviewRequested += PreviewRenumberDrawingNumbers;
        _renumberDialog.FormClosed += RenumberDialogClosed;
        _renumberDialog.Show(this);
        _renumberDialog.Activate();
    }

    private void PreviewRenumberDrawingNumbers()
    {
        if (_renumberDialog == null || _renumberCurrentJobs == null)
        {
            return;
        }

        var sorted = SortSpatially(_renumberCurrentJobs, _settings.SortOrderHorizontalFirst);
        ApplyRenumbering(sorted, _renumberDialog.Prefix, _renumberDialog.Suffix, _renumberDialog.StartNumber);
        _grid.Refresh();
        ShowRenumberPreviewOverlay(sorted);
    }

    private void RenumberDialogClosed(object? sender, FormClosedEventArgs e)
    {
        var dialog = (DrawingNumberReorderDialog)sender!;
        var currentJobs = _renumberCurrentJobs ?? new List<PlotJob>();
        var originalNumbers = _renumberOriginalNumbers ?? new Dictionary<PlotJob, string>();
        _renumberDialog = null;
        _renumberCurrentJobs = null;
        _renumberOriginalNumbers = null;

        if (dialog.DialogResult != DialogResult.OK)
        {
            // 恢复原始图号，并把 CAD 红框恢复为打印顺序数字。
            foreach (var kv in originalNumbers)
            {
                kv.Key.DrawingNumber = kv.Value;
            }
            _grid.Refresh();
            SortAndRefreshOutputPaths();
            ShowSequenceOverlayForCurrentJobs();
            dialog.Dispose();
            return;
        }

        var finalSorted = SortSpatially(currentJobs, _settings.SortOrderHorizontalFirst);
        ApplyRenumbering(finalSorted, dialog.Prefix, dialog.Suffix, dialog.StartNumber);
        foreach (var job in currentJobs)
        {
            // 图号重排后，打印顺序应重新按新图号计算，清掉右键“移到第一个”的手动优先级。
            job.SortPriority = 0;
        }
        _grid.Refresh();
        SortAndRefreshOutputPaths();
        ShowSequenceOverlayForCurrentJobs();

        // 反写 CAD 文件中的图号（批量：共享一次文档锁定/事务/图框库加载，逐张写在图多时会明显变慢）
        var updated = CadTextUpdater.UpdateDrawingNumbers(finalSorted, _currentDocument,
            failure => AppendLog("WARN", failure));

        AppendLog("INFO", $"图号重排完成，{finalSorted.Count} 张图框按" + (_settings.SortOrderHorizontalFirst ? "从左到右、从上到下" : "从上到下、从左到右") + $"排序，已反写 CAD {updated} 处。");
        dialog.Dispose();
    }

    private static void ApplyRenumbering(IReadOnlyList<PlotJob> sorted, string prefix, string suffix, int start)
    {
        var digits = Math.Max(2, (sorted.Count + start - 1).ToString().Length);
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

    /// <summary>空间排序，与矩形框批量打印共用 SpatialSorter 统一算法。</summary>
    private static List<PlotJob> SortSpatially(IReadOnlyList<PlotJob> jobs, bool horizontalFirst)
    {
        return SpatialSorter.Sort(jobs, horizontalFirst);
    }

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
            sequenceDigits);
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
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择输出目录",
            SelectedPath = _outputDirectory.Text
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
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

        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        AppendLog("INFO", "输出目录切换为 " + directory);
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
            $"将按当前勾选清单拆出 {selectedJobs.Count} 个 DWG 文件。\n\n模型空间: 新建轻量 DWG，只复制图框范围内或相交对象，打开后自动居中显示。\n布局空间: 不动模型空间，只保留当前布局并清理布局内其他图素。\n\n输出位置: {GetOutputLocationDescription()}。\n\n是否继续？",
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

            _grid.Refresh();

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
        ShowSettingsAtTab(SettingsForm.InitialTabIndex);
    }

    private void ShowSettingsAtTab(int tabIndex)
    {
        while (true)
        {
            SettingsForm.InitialTabIndex = tabIndex;
            using var form = new SettingsForm(_currentDocument);
            if (form.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            ReloadSettings();
            SortAndRefreshOutputPaths();
            AppendLog("INFO", "设置已更新。");

            if (!form.RequestPickDirectoryRowHeight && string.IsNullOrWhiteSpace(form.RequestedDirectoryColumnKey))
            {
                tabIndex = form.SelectedTabIndex;
                return;
            }

            tabIndex = form.SelectedTabIndex;
            PickDirectorySizeFromCad(form.RequestPickDirectoryRowHeight, form.RequestedDirectoryColumnKey);
        }
    }

    private void ShowSortSettings()
    {
        using var dialog = new SortOrderDialog(_settings.SortOrderHorizontalFirst);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _settings.SortOrderHorizontalFirst = _settings.SortOrderHorizontalFirst;
        AppSettingsStore.Save(_settings);

        // 按所选方向对当前清单做空间排序
        if (_jobs.Count == 0) return;
        var allJobs = _jobs.ToList();
        var layoutOrder = allJobs.Select(j => j.SpaceName).Distinct().ToList();
        var sorted = new System.Collections.Generic.List<PlotJob>();
        foreach (var space in layoutOrder)
        {
            var group = allJobs.Where(j => string.Equals(j.SpaceName, space, StringComparison.Ordinal)).ToList();
            sorted.AddRange(SpatialSorter.Sort(group, _settings.SortOrderHorizontalFirst));
        }

        _jobs.Clear();
        foreach (var job in sorted) _jobs.Add(job);
        SortAndRefreshOutputPaths();
        var orderName = _settings.SortOrderHorizontalFirst ? "从左到右，从上到下" : "从上到下，从左到右";
        AppendLog("INFO", $"已按\"{orderName}\"重排图框顺序。");
    }

    private void PickDirectorySizeFromCad(bool pickRowHeight, string? columnKey)
    {
        Hide();
        System.Windows.Forms.Application.DoEvents();
        try
        {
            var settings = AppSettingsStore.Load();
            var ok = pickRowHeight
                ? DirectoryTableGenerator.PromptRowHeight(_currentDocument, settings, out _, out var message)
                : DirectoryTableGenerator.PromptColumnSize(_currentDocument, settings, columnKey ?? "", out _, out message);
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
        _settings.PaperMatchToleranceMm = updated.PaperMatchToleranceMm;
        _settings.AddSequenceWhenPdfExists = updated.AddSequenceWhenPdfExists;
        _settings.MergePdf = updated.MergePdf;
        _settings.UseFileNameAsPdfBookmark = updated.UseFileNameAsPdfBookmark;
        _settings.MergePdfByPaperSize = updated.MergePdfByPaperSize;
        _settings.OpenOutputDirectoryAfterBatchPrint = updated.OpenOutputDirectoryAfterBatchPrint;
        _settings.OpenMergedPdfAfterMerge = updated.OpenMergedPdfAfterMerge;
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
        _mergePdfCheckBox.Checked = updated.MergePdf;
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

        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show($"未找到可用的 {SelectedOutputFormat} 输出设备。", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _mergedOutputPath = "";
        if (IsPdfOutput && _mergePdfCheckBox.Checked)
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

        SaveCurrentSettings();
        SortAndRefreshOutputPaths();
        foreach (var directory in selected
                     .Select(job => Path.GetDirectoryName(job.OutputPath))
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(directory!);
        }
        ApplyLeaveMarginSelection(selected);
        HasPendingPrint = true;
        DialogResult = DialogResult.OK;
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
            control.Enabled = plotOutput;
        }
        _mergePdfCheckBox.Enabled = IsPdfOutput;
        _marginInput.Enabled = plotOutput && _leaveMarginCheckBox.Checked;

        var outputNameColumn = _grid.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(x => string.Equals(x.DataPropertyName, nameof(PlotJob.OutputFileName), StringComparison.Ordinal));
        if (outputNameColumn != null)
        {
            outputNameColumn.HeaderText = SelectedOutputFormat + "文件名";
        }

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
        if (_printButton != null)
        {
            _printButton.Text = "停止";
            _printButton.BackColor = Color.FromArgb(200, 40, 40);
            _printButton.FlatAppearance.BorderColor = Color.FromArgb(160, 30, 30);
            _printButton.Enabled = true;
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
            System.Windows.Forms.Application.DoEvents();

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
                    AppendLog("INFO", $"开始打印 {job.DrawingNumber}_{job.Title} -> {job.OutputPath}");
                    System.Windows.Forms.Application.DoEvents();
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
                    System.Windows.Forms.Application.DoEvents();
                    var mergeInputs = selected.Select(job => new PdfMergeInput(
                        job.OutputPath,
                        Path.GetFileNameWithoutExtension(originalOutputPaths[job]),
                        job.PaperName,
                        job.PaperWidthMm,
                        job.PaperHeightMm)).ToList();
                    var mergePlans = PdfDocumentService.PlanMerges(
                        mergeInputs,
                        _mergedOutputPath,
                        _settings.MergePdfByPaperSize);
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

            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            _statusLabel.Text = $"完成，共 {printed} 张";
            var mergedFilesText = string.Join("\n", mergedOutputPaths);
            var summary = mergePdf
                ? mergedSuccessfully
                    ? $"打印并合并完成: 共 {printed} 张，生成 {mergedOutputPaths.Count} 个 PDF。\n合并文件:\n{mergedFilesText}\n日志: {_lastLogPath}"
                    : $"打印完成，但 PDF 合并未全部完成。\n成功打印 {printed} 张，失败 {failed.Count} 项，已生成 {mergedOutputPaths.Count} 个合并 PDF。\n日志: {_lastLogPath}"
                : $"打印完成: 成功 {printed} 张，失败 {failed.Count} 张。\n日志: {_lastLogPath}";
            if (failed.Count > 0)
            {
                summary += "\n\n失败项:\n" + string.Join("\n", failed);
            }

            MessageBox.Show(summary, "批量打印", MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);

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
            _lastLogPath = BatchPlotLogger.SaveRunLog(_logLines);
            MessageBox.Show($"打印已停止。\n已完成 {completed} / {selected.Count} 张。\n日志: {_lastLogPath}", "批量打印", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "打印失败";
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

            // 恢复按钮
            _printCts?.Dispose();
            _printCts = null;
            if (_printButton != null)
            {
                _printButton.Text = "开始打印";
                _printButton.BackColor = Color.FromArgb(0, 120, 215);
                _printButton.FlatAppearance.BorderColor = Color.FromArgb(0, 95, 170);
            }

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
        var leaveMargin = _leaveMarginCheckBox.Checked;
        var marginMm = ReadMarginValue(_marginInput);
        foreach (var job in jobs)
        {
            // 留白选项是本次打印设置，不改变图框识别数据，只在输出/预览时生效。
            job.LeavePaperMargin = leaveMargin;
            job.PaperMarginMm = marginMm;
            // 切换到负值（缩比例）或关闭留白时，清除上次扩大纸张模式留下的有效尺寸和精确纸张标记。
            if (!leaveMargin || marginMm <= 0)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequiresCustomPaperRegistration = false;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
            }
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
        // 预览必须使用当前输出格式对应的绘图器，确保纸张、旋转和实际输出效果一致。
        var device = SelectedPlotDevice;
        var style = _styleCombo.SelectedItem?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(device))
        {
            MessageBox.Show("请选择打印机。", "打印预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        job.LeavePaperMargin = _leaveMarginCheckBox.Checked;
        job.PaperMarginMm = ReadMarginValue(_marginInput);
        var wasVisible = Visible;
        var selectedRows = _grid.SelectedRows.Cast<DataGridViewRow>().ToList();
        var currentCell = _grid.CurrentCell;
        try
        {
            _grid.ClearSelection();
            Hide();
            System.Windows.Forms.Application.DoEvents();
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
            MessageBox.Show("打印预览失败: " + ex.Message, "打印预览", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (wasVisible && !Visible)
            {
                Show();
                Activate();
            }
            RestoreGridSelection(selectedRows, currentCell);
        }
    }

    /// <summary>
    /// 汇总本次图框库批打中的全部任意加长纸张，去重后一次性更新 LA_pdf.pmp。
    /// 扫描阶段只记录实测物理尺寸；到真正 PDF 输出前才修改用户 PMP，避免仅浏览列表也产生配置变更。
    /// </summary>
    private void PrepareCustomPaperRegistrations(IReadOnlyList<PlotJob> jobs, string deviceName)
    {
        // 每次准备前先清除所有作业上的旧扩大纸张状态，防止切换留白模式后出现陈旧标记。
        foreach (var job in jobs)
        {
            if (!job.LeavePaperMargin || job.PaperMarginMm <= 0)
            {
                job.EffectivePaperWidthMm = 0;
                job.EffectivePaperHeightMm = 0;
                job.RequiresCustomPaperRegistration = false;
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }
        }

        // 扩大纸张留白模式（PaperMarginMm > 0）：预先计算有效纸张尺寸并标记为自定义纸张。
        foreach (var job in jobs)
        {
            if (job.LeavePaperMargin && job.PaperMarginMm > 0 && job.PaperWidthMm > 0 && job.PaperHeightMm > 0)
            {
                job.EffectivePaperWidthMm = job.PaperWidthMm + job.PaperMarginMm * 2;
                job.EffectivePaperHeightMm = job.PaperHeightMm + job.PaperMarginMm * 2;
                job.RequiresCustomPaperRegistration = true;
            }
        }

        var customJobs = jobs
            .Where(job => job.RequiresCustomPaperRegistration)
            .ToList();
        if (customJobs.Count == 0)
        {
            return;
        }

        if (!string.Equals(
                deviceName,
                AcadPlotterInstaller.PreferredPdfPlotter,
                StringComparison.OrdinalIgnoreCase))
        {
            // 任意物理纸张只适用于 LA_pdf；切换到 PNG/JPG/DWF 后必须清掉之前预览留下的精确 PDF 标记。
            foreach (var job in customJobs)
            {
                job.RequireExactPaperSize = false;
                job.UseExactWindowScale = false;
                job.CustomPaperWasAdded = false;
            }
            return;
        }

        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var installedPlotter = Path.Combine(plottersDirectory, AcadPlotterInstaller.PreferredPdfPlotter);
        var installedPmp = Path.Combine(plottersDirectory, "PMP Files", "LA_pdf.pmp");
        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
        {
            var installResult = AcadPlotterInstaller.InstallBundledPlotter();
            if (!installResult.Installed)
                throw new InvalidOperationException("LA_pdf 打印机配置不完整: " + installResult.Message);

            plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
            installedPlotter = Path.Combine(plottersDirectory, AcadPlotterInstaller.PreferredPdfPlotter);
            installedPmp = Path.Combine(plottersDirectory, "PMP Files", "LA_pdf.pmp");
        }

        if (!File.Exists(installedPlotter) || !File.Exists(installedPmp))
            throw new FileNotFoundException("LA_pdf.pc3/pc5 或 LA_pdf.pmp 不存在，无法批量注册任意纸张。", installedPmp);

        var requests = customJobs
            .Select(job => new PmpCustomPaper.PaperRequest
            {
                WidthMm = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm,
                HeightMm = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm
            })
            .ToList();
        var registrations = PmpCustomPaper.RegisterCustomPapers(installedPmp, requests)
            ?? throw new InvalidOperationException("LA_pdf.pmp 批量注册任意加长纸张失败，已停止打印，避免回退到错误纸张。");
        var anyAdded = registrations.Any(registration => registration.WasAdded);

#if AUTOCAD
        if (!AcadPlotterInstaller.EnsurePmpAttachment(
                installedPlotter,
                installedPmp,
                forceRewrite: anyAdded,
                out var attachmentMessage))
        {
            throw new InvalidOperationException("LA_pdf.pc3 关联批量 PMP 失败：" + attachmentMessage);
        }
        AppendLog("INFO", "AutoCAD 批量任意纸张关联刷新: " + attachmentMessage);
#endif

        foreach (var job in customJobs)
        {
            // 任意加长图必须按实测纸张精确选纸和缩放；禁止名称匹配或相近纸张回退。
            job.RequireExactPaperSize = true;
            job.UseExactWindowScale = true;
            job.CustomPaperWasAdded = false;
        }

        if (anyAdded)
        {
            // AutoCAD/ZWCAD 的介质目录按模型/布局分别缓存；每类空间只让第一张触发一次重载。
            foreach (var firstJob in customJobs.GroupBy(job => job.IsPaperSpace).Select(group => group.First()))
                firstJob.CustomPaperWasAdded = true;
        }

        var sizes = string.Join(", ", registrations.Select(registration =>
            $"{registration.WidthMm:0.######}x{registration.HeightMm:0.######}mm({(registration.WasAdded ? "新增" : "复用")})"));
        AppendLog("INFO", $"任意加长纸张已一次性准备，共 {registrations.Count} 种: {sizes}");
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
            // 预览窗口退出后 CAD/WinForms 可能重置选择状态，恢复失败不影响打印主流程。
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
        if (numberChanged)
        {
            // 手工修改图号后，列表编号、实际打印顺序和 CAD 红框顺序都要按新图号刷新。
            job.SortPriority = 0;
        }
        SortAndRefreshOutputPaths();
    }

    private void AppendLog(string level, string message)
    {
        _logLines.Add(BatchPlotLogger.Format(level, message));
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!HasPendingPrint)
        {
            _renumberDialog?.Close();
            ClearSequenceOverlay();
        }

        SaveCurrentSettings();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 窗体销毁时退订事件并释放覆盖层（内部会退订 CAD 文档事件），防止关窗后仍拦截 ERASE 命令
            _sequenceOverlay.FrameErased -= SequenceOverlayFrameErased;
            _sequenceOverlay.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SaveCurrentSettings()
    {
        _settings.LastPlotDevice = AcadPlotterInstaller.PreferredPdfPlotter;
        _settings.LastStyleSheet = _styleCombo.SelectedItem?.ToString() ?? "";
        _settings.MergePdf = _mergePdfCheckBox.Checked;
        _settings.LeavePaperMargin = _leaveMarginCheckBox.Checked;
        _settings.PaperMarginMm = ReadMarginValue(_marginInput);
        AppSettingsStore.Save(_settings);
    }

    /// <summary>留白下拉列表选项，+ 为扩大纸张，- 为缩比例。</summary>
    private sealed class MarginOption
    {
        public double Value { get; set; }
        public override string ToString() => Value > 0
            ? $"+ {Value:0.#} mm"
            : $"- {Math.Abs(Value):0.#} mm";
    }

    /// <summary>初始化留白下拉列表，正值=扩大纸张，负值=缩比例，整数1~10配对显示。</summary>
    internal static void InitMarginCombo(ComboBox combo, int width, double savedValue)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
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
    internal static double ReadMarginValue(ComboBox combo)
        => combo.SelectedItem is MarginOption opt ? opt.Value : 1.0;
}
