using System;
using System.Collections.Generic;
using System.Drawing;
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

public sealed class SettingsForm : Form
{
    private const string DefaultTextStyleDisplay = "(默认)";

    private readonly Document? _document;
    private readonly CheckBox _rememberOutput = new();
    private readonly TextBox _outputSubfolder = new();
    private readonly CheckBox _autoScan = new();
    private readonly NumericUpDown _paperTolerance = new();
    private readonly CheckBox _allowPaperNameFallback = new();
    private readonly CheckBox _showProgress = new();
    private readonly CheckBox _addSequenceWhenPdfExists = new();
    private readonly CheckBox _openExternalDwgForPlot = new();
    private readonly NumericUpDown _directoryIndexWidth = new();
    private readonly NumericUpDown _directoryNumberWidth = new();
    private readonly NumericUpDown _directoryTitleWidth = new();
    private readonly NumericUpDown _directoryPaperWidth = new();
    private readonly NumericUpDown _directoryRemarkWidth = new();
    private readonly NumericUpDown _directoryRowHeight = new();
    private readonly NumericUpDown _directoryTextRatio = new();
    private readonly ComboBox _directoryTextStyle = new();

    // 文件名设置
    private readonly TextBox _fileNameSeparator = new();
    private readonly Label _fileNamePreview = new();
    private readonly ListBox _availableFields = new();
    private readonly ListBox _selectedFields = new();

    public bool RequestPickDirectoryCellSizes { get; private set; }

    public SettingsForm(Document? document = null)
    {
        _document = document;
        InitializeComponents();
        LoadSettings();
    }

    private void InitializeComponents()
    {
        Text = "批量打印设置";
        UiLayout.ConfigureForm(this, 680, 560, 680, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(14))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(38)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildGeneralTab());
        tabs.TabPages.Add(BuildFileNameTab());
        tabs.TabPages.Add(BuildDirectoryTab());

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "目录表格会写入当前 CAD 当前空间，文字大小按单元格高度和列宽自动反推。"
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = UiLayout.CreateButton("保存", 82);
        save.Click += (_, _) => SaveSettings();
        var reset = UiLayout.CreateButton("恢复默认", 96);
        reset.Click += (_, _) => ResetDefaults();
        var cancel = UiLayout.CreateButton("取消", 82);
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(reset);

        root.Controls.Add(tabs, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("常规");
        var table = CreateSettingsTable(8);

        _rememberOutput.Text = "记住上次输出目录";
        _rememberOutput.AutoSize = true;
        _rememberOutput.Dock = DockStyle.Fill;

        _outputSubfolder.Dock = DockStyle.Fill;

        _autoScan.Text = "打开窗口不自动扫描，点击扫描才扫描";
        _autoScan.AutoSize = true;
        _autoScan.Dock = DockStyle.Fill;
        _autoScan.Enabled = false;

        ConfigureNumber(_paperTolerance, 0.5M, 20M, 0.5M, 1);

        _allowPaperNameFallback.Text = "尺寸匹配失败时允许按 A0/A1/A2/A3 名称兜底";
        _allowPaperNameFallback.AutoSize = true;
        _allowPaperNameFallback.Dock = DockStyle.Fill;

        _showProgress.Text = "打印时显示 CAD 打印进度窗口";
        _showProgress.AutoSize = true;
        _showProgress.Dock = DockStyle.Fill;

        _addSequenceWhenPdfExists.Text = "PDF 已存在时自动加序号";
        _addSequenceWhenPdfExists.AutoSize = true;
        _addSequenceWhenPdfExists.Dock = DockStyle.Fill;

        _openExternalDwgForPlot.Text = "跨文件打印时临时打开 DWG";
        _openExternalDwgForPlot.AutoSize = true;
        _openExternalDwgForPlot.Dock = DockStyle.Fill;

        UiLayout.AddRow(table, 0, "", _rememberOutput);
        UiLayout.AddRow(table, 1, "默认输出子文件夹", _outputSubfolder);
        UiLayout.AddRow(table, 2, "", _autoScan);
        UiLayout.AddRow(table, 3, "纸张匹配容差(mm)", _paperTolerance);
        UiLayout.AddRow(table, 4, "", _allowPaperNameFallback);
        UiLayout.AddRow(table, 5, "", _showProgress);
        UiLayout.AddRow(table, 6, "", _addSequenceWhenPdfExists);
        UiLayout.AddRow(table, 7, "", _openExternalDwgForPlot);
        page.Controls.Add(table);
        return page;
    }

    private static readonly (string Key, string Display)[] AllFileNameFields =
    {
        ("DrawingNumber", "图号"),
        ("Title", "图名"),
        ("Date", "日期"),
        ("Revision", "版次"),
        ("Phase", "设计阶段"),
        ("PaperName", "纸张尺寸"),
    };

    private TabPage BuildFileNameTab()
    {
        var page = new TabPage("文件名");
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(12))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(170)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 连接符
        _fileNameSeparator.Text = "_";
        _fileNameSeparator.Dock = DockStyle.Left;
        _fileNameSeparator.Width = UiLayout.Scale(120);
        _fileNameSeparator.TextChanged += (_, _) => UpdateFileNamePreview();
        UiLayout.AddRow(table, 0, "字段连接符", _fileNameSeparator);

        // 双列表：可用字段 → 已选字段
        var fieldLabel = new Label
        {
            Text = "已选字段（按顺序输出）:",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
            MinimumSize = new Size(0, UiLayout.Scale(200))
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(56)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(22)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 列标题
        panel.Controls.Add(new Label { Text = "可用字段", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, ForeColor = Color.DimGray }, 0, 0);
        panel.Controls.Add(new Label(), 1, 0);
        panel.Controls.Add(new Label { Text = "已选字段", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, ForeColor = Color.DimGray }, 2, 0);

        // 左：可用字段
        _availableFields.Dock = DockStyle.Fill;
        _availableFields.IntegralHeight = false;
        panel.Controls.Add(_availableFields, 0, 1);

        // 中：添加/移除按钮
        var midButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
            Padding = new Padding(UiLayout.Scale(4), UiLayout.Scale(10), UiLayout.Scale(4), 0)
        };
        var addBtn = UiLayout.CreateButton("添加 →", 72);
        addBtn.Click += (_, _) => MoveSelectedItems(_availableFields, _selectedFields);
        var removeBtn = UiLayout.CreateButton("← 移除", 72);
        removeBtn.Click += (_, _) => MoveSelectedItems(_selectedFields, _availableFields);
        midButtons.Controls.Add(addBtn);
        midButtons.Controls.Add(removeBtn);
        panel.Controls.Add(midButtons, 1, 1);

        // 右：已选字段 + 上移/下移
        var rightPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(40)));

        _selectedFields.Dock = DockStyle.Fill;
        _selectedFields.IntegralHeight = false;
        rightPanel.Controls.Add(_selectedFields, 0, 0);

        var sortButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.TopDown,
            Padding = new Padding(UiLayout.Scale(2), UiLayout.Scale(10), 0, 0)
        };
        var upBtn = UiLayout.CreateButton("▲", 36);
        upBtn.Click += (_, _) => { MoveSelectedFieldUp(); UpdateFileNamePreview(); };
        var downBtn = UiLayout.CreateButton("▼", 36);
        downBtn.Click += (_, _) => { MoveSelectedFieldDown(); UpdateFileNamePreview(); };
        sortButtons.Controls.Add(upBtn);
        sortButtons.Controls.Add(downBtn);
        rightPanel.Controls.Add(sortButtons, 1, 0);

        panel.Controls.Add(rightPanel, 2, 1);

        table.Controls.Add(fieldLabel, 0, 1);
        table.Controls.Add(panel, 1, 1);

        // 预览
        _fileNamePreview.Dock = DockStyle.Fill;
        _fileNamePreview.TextAlign = ContentAlignment.MiddleLeft;
        _fileNamePreview.ForeColor = Color.DimGray;
        _fileNamePreview.AutoSize = true;
        table.Controls.Add(_fileNamePreview, 1, 2);

        table.RowCount = 3;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));

        page.Controls.Add(table);
        return page;
    }

    private void MoveSelectedItems(ListBox from, ListBox to)
    {
        var selected = from.SelectedItems.Cast<FileNameFieldItem>().ToList();
        // 目标列表已有的 key 不再添加，防止重复
        var existingKeys = new HashSet<string>(
            to.Items.Cast<FileNameFieldItem>().Select(x => x.Key),
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (existingKeys.Add(item.Key))
            {
                from.Items.Remove(item);
                to.Items.Add(item);
            }
        }
        if (from.Items.Count > 0) from.SelectedIndex = 0;
        if (to.Items.Count > 0) to.SelectedIndex = to.Items.Count - 1;
        UpdateFileNamePreview();
    }

    private void MoveSelectedFieldUp()
    {
        var idx = _selectedFields.SelectedIndex;
        if (idx <= 0) return;
        var item = _selectedFields.Items[idx];
        _selectedFields.Items.RemoveAt(idx);
        _selectedFields.Items.Insert(idx - 1, item);
        _selectedFields.SelectedIndex = idx - 1;
    }

    private void MoveSelectedFieldDown()
    {
        var idx = _selectedFields.SelectedIndex;
        if (idx < 0 || idx >= _selectedFields.Items.Count - 1) return;
        var item = _selectedFields.Items[idx];
        _selectedFields.Items.RemoveAt(idx);
        _selectedFields.Items.Insert(idx + 1, item);
        _selectedFields.SelectedIndex = idx + 1;
    }

    private void UpdateFileNamePreview()
    {
        var separator = _fileNameSeparator.Text;
        var parts = _selectedFields.Items.Cast<FileNameFieldItem>().Select(x => x.Display).ToList();
        _fileNamePreview.Text = parts.Count > 0
            ? "示例: " + string.Join(separator, parts)
            : "(请添加至少一个字段)";
    }

    private void LoadFileNameFields(IReadOnlyList<string> fieldKeys)
    {
        _availableFields.Items.Clear();
        _selectedFields.Items.Clear();

        // 去重：已见过的 key 跳过，避免重复
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in fieldKeys)
        {
            if (!seen.Add(key)) continue;
            var match = AllFileNameFields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match != default)
            {
                _selectedFields.Items.Add(new FileNameFieldItem(match.Key, match.Display));
            }
        }
        // 其余加入可用列表
        foreach (var (key, display) in AllFileNameFields)
        {
            if (!seen.Contains(key))
            {
                _availableFields.Items.Add(new FileNameFieldItem(key, display));
            }
        }

        UpdateFileNamePreview();
    }

    private sealed class FileNameFieldItem
    {
        public string Key { get; }
        public string Display { get; }

        public FileNameFieldItem(string key, string display)
        {
            Key = key;
            Display = display;
        }

        public override string ToString() => Display;
    }

    private TabPage BuildDirectoryTab()
    {
        var page = new TabPage("图纸目录");
        var table = CreateSettingsTable(10);

        ConfigureNumber(_directoryIndexWidth, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryNumberWidth, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryTitleWidth, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryPaperWidth, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryRemarkWidth, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryRowHeight, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryTextRatio, 0.1M, 0.9M, 0.01M, 2);
        _directoryTextStyle.DropDownStyle = ComboBoxStyle.DropDownList;
        _directoryTextStyle.Dock = DockStyle.Left;
        _directoryTextStyle.Width = UiLayout.Scale(220);
        LoadTextStyles();

        var pickButton = UiLayout.CreateButton("从 CAD 框选单元格尺寸", 180);
        pickButton.Click += (_, _) => PickDirectoryCellSizes();
        pickButton.Enabled = _document != null;

        UiLayout.AddRow(table, 0, "序号列宽", _directoryIndexWidth);
        UiLayout.AddRow(table, 1, "图号列宽", _directoryNumberWidth);
        UiLayout.AddRow(table, 2, "图名列宽", _directoryTitleWidth);
        UiLayout.AddRow(table, 3, "图幅列宽", _directoryPaperWidth);
        UiLayout.AddRow(table, 4, "备注列宽", _directoryRemarkWidth);
        UiLayout.AddRow(table, 5, "行高", _directoryRowHeight);
        UiLayout.AddRow(table, 6, "文字高度比例", _directoryTextRatio);
        UiLayout.AddRow(table, 7, "目录文字样式", _directoryTextStyle);
        UiLayout.AddRow(table, 8, "", pickButton);
        UiLayout.AddRow(table, 9, "", new Label
        {
            Text = "框选时依次选择：序号、图号、图名、图幅、备注单元格。行高取第一个单元格高度。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        });

        page.Controls.Add(table);
        return page;
    }

    private static TableLayoutPanel CreateSettingsTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            Padding = new Padding(UiLayout.Scale(12))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(170)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void ConfigureNumber(NumericUpDown input, decimal min, decimal max, decimal increment, int decimals)
    {
        input.DecimalPlaces = decimals;
        input.Minimum = min;
        input.Maximum = max;
        input.Increment = increment;
        input.Dock = DockStyle.Left;
        input.Width = UiLayout.Scale(130);
    }


    private void LoadSettings()
    {
        Apply(AppSettingsStore.Load());
    }

    private void Apply(AppSettings settings)
    {
        _rememberOutput.Checked = settings.RememberLastOutputDirectory;
        _outputSubfolder.Text = settings.DefaultOutputSubfolder;
        _autoScan.Checked = false;
        _paperTolerance.Value = UiLayout.Clamp(_paperTolerance, settings.PaperMatchToleranceMm);
        _allowPaperNameFallback.Checked = settings.AllowStandardPaperNameFallback;
        _showProgress.Checked = settings.ShowPlotProgress;
        _addSequenceWhenPdfExists.Checked = settings.AddSequenceWhenPdfExists;
        _fileNameSeparator.Text = string.IsNullOrWhiteSpace(settings.PdfFileNameSeparator) ? "_" : settings.PdfFileNameSeparator;
        LoadFileNameFields(settings.PdfFileNameFields);
        _openExternalDwgForPlot.Checked = settings.OpenExternalDwgForPlot;
        _directoryIndexWidth.Value = UiLayout.Clamp(_directoryIndexWidth, settings.DirectoryIndexWidth);
        _directoryNumberWidth.Value = UiLayout.Clamp(_directoryNumberWidth, settings.DirectoryNumberWidth);
        _directoryTitleWidth.Value = UiLayout.Clamp(_directoryTitleWidth, settings.DirectoryTitleWidth);
        _directoryPaperWidth.Value = UiLayout.Clamp(_directoryPaperWidth, settings.DirectoryPaperWidth);
        _directoryRemarkWidth.Value = UiLayout.Clamp(_directoryRemarkWidth, settings.DirectoryRemarkWidth);
        _directoryRowHeight.Value = UiLayout.Clamp(_directoryRowHeight, settings.DirectoryRowHeight);
        _directoryTextRatio.Value = UiLayout.Clamp(_directoryTextRatio, settings.DirectoryTextHeightRatio);
        SelectTextStyle(settings.DirectoryTextStyleName);
    }

    private void SaveSettings()
    {
        var current = ReadSettingsFromControls();
        AppSettingsStore.Save(current);
        DialogResult = DialogResult.OK;
        Close();
    }

    private AppSettings ReadSettingsFromControls()
    {
        var current = AppSettingsStore.Load();
        current.RememberLastOutputDirectory = _rememberOutput.Checked;
        current.DefaultOutputSubfolder = string.IsNullOrWhiteSpace(_outputSubfolder.Text) ? "PDF" : _outputSubfolder.Text.Trim();
        current.AutoScanCurrentDrawing = false;
        current.PaperMatchToleranceMm = (double)_paperTolerance.Value;
        current.AllowStandardPaperNameFallback = _allowPaperNameFallback.Checked;
        current.ShowPlotProgress = _showProgress.Checked;
        current.AddSequenceWhenPdfExists = _addSequenceWhenPdfExists.Checked;
        current.PdfFileNameSeparator = string.IsNullOrWhiteSpace(_fileNameSeparator.Text) ? "_" : _fileNameSeparator.Text.Trim();
        current.PdfFileNameFields = _selectedFields.Items
            .Cast<FileNameFieldItem>()
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        current.OpenExternalDwgForPlot = _openExternalDwgForPlot.Checked;
        current.DirectoryIndexWidth = (double)_directoryIndexWidth.Value;
        current.DirectoryNumberWidth = (double)_directoryNumberWidth.Value;
        current.DirectoryTitleWidth = (double)_directoryTitleWidth.Value;
        current.DirectoryPaperWidth = (double)_directoryPaperWidth.Value;
        current.DirectoryRemarkWidth = (double)_directoryRemarkWidth.Value;
        current.DirectoryRowHeight = (double)_directoryRowHeight.Value;
        current.DirectoryTextHeightRatio = (double)_directoryTextRatio.Value;
        current.DirectoryTextStyleName = _directoryTextStyle.SelectedItem?.ToString() == DefaultTextStyleDisplay
            ? ""
            : _directoryTextStyle.SelectedItem?.ToString() ?? "";
        return current;
    }

    private void LoadTextStyles()
    {
        _directoryTextStyle.Items.Clear();
        _directoryTextStyle.Items.Add(DefaultTextStyleDisplay);

        if (_document != null)
        {
            try
            {
                using var tr = _document.Database.TransactionManager.StartTransaction();
                var table = (TextStyleTable)tr.GetObject(_document.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.IsNullOrWhiteSpace(record.Name))
                    {
                        _directoryTextStyle.Items.Add(record.Name);
                    }
                }

                tr.Commit();
            }
            catch
            {
            }
        }

        if (_directoryTextStyle.Items.Count > 0)
        {
            _directoryTextStyle.SelectedIndex = 0;
        }
    }

    private void SelectTextStyle(string? name)
    {
        var target = string.IsNullOrWhiteSpace(name) ? DefaultTextStyleDisplay : name;
        for (var i = 0; i < _directoryTextStyle.Items.Count; i++)
        {
            if (string.Equals(_directoryTextStyle.Items[i]?.ToString(), target, StringComparison.OrdinalIgnoreCase))
            {
                _directoryTextStyle.SelectedIndex = i;
                return;
            }
        }

        if (_directoryTextStyle.Items.Count > 0)
        {
            _directoryTextStyle.SelectedIndex = 0;
        }
    }

    private void PickDirectoryCellSizes()
    {
        if (_document == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        AppSettingsStore.Save(ReadSettingsFromControls());
        RequestPickDirectoryCellSizes = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetDefaults()
    {
        Apply(AppSettingsStore.Default());
    }
}
