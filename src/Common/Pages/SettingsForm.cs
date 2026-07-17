using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
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
    private readonly ComboBox _directoryColorIndex = new();
    private readonly NumericUpDown _directoryTextHeight = new();
    private readonly NumericUpDown _directoryTextWidthFactor = new();
    private readonly NumericUpDown _directoryRowHeight = new();
    private readonly ComboBox _directoryTextStyle = new();
    private readonly TextBox _directoryLayerName = new();
    private readonly CheckBox _directoryDrawHeader = new();
    private readonly CheckBox _directoryDrawGridLines = new();
    private readonly DataGridView _directoryColumnsGrid = new();
    private readonly DirectoryPreviewControl _directoryOrderPreview = new();

    // 文件名设置
    private readonly ComboBox _fileNameSeparator = new();
    private readonly Label _fileNamePreview = new();
    private readonly ListBox _availableFields = new();
    private readonly ListBox _selectedFields = new();
    private readonly NumericUpDown _fileNameSequenceDigits = new();

    public string? RequestedDirectoryColumnKey { get; private set; }
    public bool RequestPickDirectoryRowHeight { get; private set; }

    /// <summary>
    /// 设置/获取下次打开设置窗口时默认显示的标签页索引（0=常规, 1=文件名, 2=图纸目录）。
    /// 调用方在窗体关闭后读取 <see cref="SelectedTabIndex"/> 并传入下一次构造，实现图中交互后回到原标签页。
    /// </summary>
    public static int InitialTabIndex { get; set; }

    /// <summary>
    /// 窗体关闭前记录当前标签页索引，供调用方传给 <see cref="InitialTabIndex"/>。
    /// </summary>
    public int SelectedTabIndex { get; private set; }

    private TabControl _tabs = null!;

    public SettingsForm(Document? document = null)
    {
        _document = document;
        InitializeComponents();
        LoadSettings();
    }

    private void InitializeComponents()
    {
        Text = "批量打印设置";
        UiLayout.ConfigureForm(this, 760, 600, 680, 540);
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(24)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildFileNameTab());
        _tabs.TabPages.Add(BuildDirectoryTab());

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "图纸目录会写入当前 CAD 当前空间；目录列与批量打印实际识别出的图框字段保持一致。"
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

        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        // 恢复上次关闭时的标签页（如从 CAD 交互返回后回到"图纸目录"而非"常规"）
        if (InitialTabIndex >= 0 && InitialTabIndex < _tabs.TabCount)
        {
            _tabs.SelectedIndex = InitialTabIndex;
        }
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

    /// <summary>
    /// 文件名可用的全部字段。与 FileNameSanitizer.GetFileNameParts 中的 key 保持一一对应。
    /// </summary>
    private static readonly (string Key, string Display)[] AllFileNameFields =
    {
        ("DrawingNumber", "图号"),
        ("Title", "图名"),
        ("Date", "日期"),
        ("Revision", "版次"),
        ("Phase", "设计阶段"),
        ("Info1", "信息1"),
        ("Info2", "信息2"),
        ("PaperName", "纸张尺寸"),
        ("Sequence", "序号"),
    };

    private TabPage BuildFileNameTab()
    {
        var page = new TabPage("文件名");
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(145)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 连接符使用固定下拉选项，空格单独显示说明，避免用户误以为没有选中。
        _fileNameSeparator.DropDownStyle = ComboBoxStyle.DropDownList;
        _fileNameSeparator.Items.AddRange(new object[]
        {
            new FileNameSeparatorItem("_", "_（下划线）"),
            new FileNameSeparatorItem(" ", "空格"),
            new FileNameSeparatorItem("+", "+（加号）"),
            new FileNameSeparatorItem("-", "-（短横线）"),
            new FileNameSeparatorItem(".", ".（点号）"),
            new FileNameSeparatorItem("~", "~（波浪线）"),
            new FileNameSeparatorItem("=", "=（等号）"),
            new FileNameSeparatorItem("", "无连接符")
        });
        _fileNameSeparator.SelectedIndex = 0;
        _fileNameSeparator.Dock = DockStyle.Left;
        _fileNameSeparator.Width = UiLayout.Scale(160);
        _fileNameSeparator.SelectedIndexChanged += (_, _) => UpdateFileNamePreview();
        UiLayout.AddRow(table, 0, "字段连接符", _fileNameSeparator);

        // 序号补零位数，默认 2 位 → 01, 02, …
        ConfigureNumber(_fileNameSequenceDigits, 1, 10, 1, 0);
        _fileNameSequenceDigits.Dock = DockStyle.Left;
        _fileNameSequenceDigits.Width = UiLayout.Scale(80);
        var seqRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false
        };
        seqRow.Controls.Add(_fileNameSequenceDigits);
        seqRow.Controls.Add(new Label
        {
            Text = "位（例：2→01，3→001）",
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            AutoSize = true
        });
        UiLayout.AddRow(table, 1, "序号补零位数", seqRow);

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
        // 中间操作按钮实际宽度大于原列宽，预留足够空间避免按钮压到右侧“已选字段”列表。
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(92)));
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
            WrapContents = false,
            AutoScroll = false,
            Padding = new Padding(UiLayout.Scale(6), UiLayout.Scale(10), UiLayout.Scale(6), 0)
        };
        var addBtn = UiLayout.CreateButton("添加 →", 76);
        addBtn.Margin = new Padding(0, 0, 0, UiLayout.Scale(6));
        addBtn.Click += (_, _) => MoveSelectedItems(_availableFields, _selectedFields);
        var removeBtn = UiLayout.CreateButton("← 移除", 76);
        removeBtn.Margin = Padding.Empty;
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

        table.Controls.Add(fieldLabel, 0, 2);
        table.Controls.Add(panel, 1, 2);

        // 预览
        _fileNamePreview.Dock = DockStyle.Fill;
        _fileNamePreview.TextAlign = ContentAlignment.MiddleLeft;
        _fileNamePreview.ForeColor = Color.DimGray;
        _fileNamePreview.AutoSize = true;
        table.Controls.Add(_fileNamePreview, 1, 3);

        table.RowCount = 4;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));

        page.Controls.Add(table);
        return page;
    }

    private void MoveSelectedItems(ListBox from, ListBox to)
    {
        var selected = from.SelectedItems.Cast<FileNameFieldItem>().ToList();
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
        _selectedFields.BeginUpdate();
        try
        {
            _selectedFields.Items.RemoveAt(idx);
            _selectedFields.Items.Insert(idx - 1, item);
            _selectedFields.SelectedIndex = idx - 1;
        }
        finally
        {
            _selectedFields.EndUpdate();
        }
        UpdateFileNamePreview();
    }

    private void MoveSelectedFieldDown()
    {
        var idx = _selectedFields.SelectedIndex;
        if (idx < 0 || idx >= _selectedFields.Items.Count - 1) return;
        var item = _selectedFields.Items[idx];
        _selectedFields.BeginUpdate();
        try
        {
            _selectedFields.Items.RemoveAt(idx);
            _selectedFields.Items.Insert(idx + 1, item);
            _selectedFields.SelectedIndex = idx + 1;
        }
        finally
        {
            _selectedFields.EndUpdate();
        }
        UpdateFileNamePreview();
    }

    private void UpdateFileNamePreview()
    {
        var separator = SelectedFileNameSeparator();
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
        // 其余加入可用列表（图号不可移到可用列表）
        foreach (var (key, display) in AllFileNameFields)
        {
            if (!seen.Contains(key))
            {
                _availableFields.Items.Add(new FileNameFieldItem(key, display));
            }
        }

        UpdateFileNamePreview();
    }

    private sealed class FileNameSeparatorItem
    {
        public string Value { get; }
        private string Display { get; }

        public FileNameSeparatorItem(string value, string display)
        {
            Value = value;
            Display = display;
        }

        public override string ToString() => Display;
    }

    private string SelectedFileNameSeparator()
    {
        return _fileNameSeparator.SelectedItem is FileNameSeparatorItem item ? item.Value : "_";
    }

    private void SelectFileNameSeparator(string? separator)
    {
        var expected = AppSettingsStore.NormalizeFileNameSeparator(separator);
        for (var index = 0; index < _fileNameSeparator.Items.Count; index++)
        {
            if (_fileNameSeparator.Items[index] is FileNameSeparatorItem item
                && string.Equals(item.Value, expected, StringComparison.Ordinal))
            {
                _fileNameSeparator.SelectedIndex = index;
                return;
            }
        }

        _fileNameSeparator.SelectedIndex = 0;
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
        var page = new TabPage("图纸目录") { Padding = new Padding(UiLayout.Scale(5)) };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(142)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        ConfigureDirectoryColorIndex();
        ConfigureNumber(_directoryTextHeight, 1, 1000000, 10, 2);
        ConfigureNumber(_directoryTextWidthFactor, 0.1M, 10, 0.05M, 2);
        ConfigureNumber(_directoryRowHeight, 1, 1000000, 10, 2);
        _directoryTextHeight.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryTextWidthFactor.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryRowHeight.ValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryTextStyle.DropDownStyle = ComboBoxStyle.DropDownList;
        _directoryTextStyle.Dock = DockStyle.Fill;
        _directoryTextStyle.SelectedIndexChanged += (_, _) => UpdateDirectoryPreview();
        LoadTextStyles();
        _directoryLayerName.Dock = DockStyle.Fill;

        var parameterGroup = new GroupBox
        {
            Text = "目录字体及绘制相关设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(6))
        };
        var parameters = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 2
        };
        for (var i = 0; i < 6; i++)
        {
            parameters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16.666F));
        }
        parameters.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(88)));
        parameters.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(28)));

        parameters.Controls.Add(BuildDirectoryParameter("颜色索引", _directoryColorIndex), 0, 0);
        parameters.Controls.Add(BuildDirectoryParameter("文字高度", _directoryTextHeight), 1, 0);
        parameters.Controls.Add(BuildDirectoryParameter("宽度因子", _directoryTextWidthFactor), 2, 0);
        parameters.Controls.Add(BuildDirectoryParameter("文字样式", _directoryTextStyle), 3, 0);
        parameters.Controls.Add(BuildDirectoryParameter("图层名称", _directoryLayerName), 4, 0);
        parameters.Controls.Add(BuildDirectoryRowHeightParameter(), 5, 0);

        _directoryDrawHeader.Text = "绘制目录表头";
        _directoryDrawHeader.AutoSize = true;
        _directoryDrawGridLines.Text = "绘制目录框线";
        _directoryDrawGridLines.AutoSize = true;
        var drawOptions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false
        };
        drawOptions.Controls.Add(_directoryDrawHeader);
        drawOptions.Controls.Add(_directoryDrawGridLines);
        parameters.Controls.Add(drawOptions, 0, 1);
        parameters.SetColumnSpan(drawOptions, 6);
        parameterGroup.Controls.Add(parameters);

        var contentGroup = new GroupBox
        {
            Text = "目录内容设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(6))
        };
        ConfigureDirectoryColumnsGrid();
        var contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(19)));
        contentLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(76)));
        contentLayout.Controls.Add(_directoryColumnsGrid, 0, 0);
        contentLayout.Controls.Add(new Label
        {
            Text = "顺序预览（按实际列宽、行高和字高等比例缩放）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            ForeColor = Color.DimGray
        }, 0, 1);
        contentLayout.Controls.Add(_directoryOrderPreview, 0, 2);
        contentGroup.Controls.Add(contentLayout);

        root.Controls.Add(parameterGroup, 0, 0);
        root.Controls.Add(contentGroup, 0, 1);
        page.Controls.Add(root);
        return page;
    }

    private static Control BuildDirectoryParameter(string label, Control input)
    {
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, UiLayout.Scale(2), UiLayout.Scale(4), 0);
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(20)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        panel.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        panel.Controls.Add(input, 0, 1);
        return panel;
    }

    private Control BuildDirectoryRowHeightParameter()
    {
        _directoryRowHeight.Dock = DockStyle.Fill;
        _directoryRowHeight.Margin = new Padding(0, UiLayout.Scale(2), UiLayout.Scale(4), 0);
        var pickHeight = UiLayout.CreateButton("图中交互", 68);
        pickHeight.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        pickHeight.Enabled = _document != null;
        pickHeight.Click += (_, _) => RequestRowHeightFromCad();

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(20)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(27)));
        panel.Controls.Add(new Label
        {
            Text = "目录行高",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft
        }, 0, 0);
        panel.Controls.Add(_directoryRowHeight, 0, 1);
        panel.Controls.Add(pickHeight, 0, 2);
        return panel;
    }

    private void ConfigureDirectoryColorIndex()
    {
        _directoryColorIndex.DropDownStyle = ComboBoxStyle.DropDownList;
        _directoryColorIndex.DrawMode = DrawMode.OwnerDrawFixed;
        _directoryColorIndex.ItemHeight = UiLayout.Scale(20);
        _directoryColorIndex.MaxDropDownItems = 14;
        _directoryColorIndex.IntegralHeight = false;
        _directoryColorIndex.DropDownHeight = UiLayout.Scale(282);
        _directoryColorIndex.DrawItem += DrawDirectoryColorIndex;
        for (var index = 0; index <= 256; index++)
        {
            _directoryColorIndex.Items.Add(new DirectoryColorItem(index, GetAciPreviewColor(index)));
        }
    }

    private void DrawDirectoryColorIndex(object? sender, DrawItemEventArgs e)
    {
        e.DrawBackground();
        if (e.Index < 0 || e.Index >= _directoryColorIndex.Items.Count
            || _directoryColorIndex.Items[e.Index] is not DirectoryColorItem item)
        {
            return;
        }

        var swatchSize = Math.Max(UiLayout.Scale(12), e.Bounds.Height - UiLayout.Scale(6));
        var swatch = new Rectangle(
            e.Bounds.Left + UiLayout.Scale(3),
            e.Bounds.Top + (e.Bounds.Height - swatchSize) / 2,
            swatchSize,
            swatchSize);
        using (var brush = new SolidBrush(item.Color))
        {
            e.Graphics.FillRectangle(brush, swatch);
        }
        e.Graphics.DrawRectangle(Pens.DimGray, swatch);

        var text = item.Index switch
        {
            0 => "0（随块）",
            256 => "256（随层）",
            _ => item.Index.ToString(CultureInfo.InvariantCulture)
        };
        var textColor = (e.State & DrawItemState.Selected) != 0
            ? SystemColors.HighlightText
            : SystemColors.ControlText;
        TextRenderer.DrawText(
            e.Graphics,
            text,
            e.Font ?? Font,
            new Point(swatch.Right + UiLayout.Scale(5), e.Bounds.Top + (e.Bounds.Height - Font.Height) / 2),
            textColor);
        e.DrawFocusRectangle();
    }

    private static Color GetAciPreviewColor(int index)
    {
        var fixedColors = new[]
        {
            Color.DimGray,
            Color.FromArgb(255, 0, 0),
            Color.FromArgb(255, 255, 0),
            Color.FromArgb(0, 255, 0),
            Color.FromArgb(0, 255, 255),
            Color.FromArgb(0, 0, 255),
            Color.FromArgb(255, 0, 255),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(128, 128, 128),
            Color.FromArgb(192, 192, 192)
        };
        if (index >= 0 && index < fixedColors.Length)
        {
            return fixedColors[index];
        }

        if (index >= 10 && index <= 249)
        {
            // ACI 10～249 每 10 个索引为一个色相组，偶数为纯色、奇数为同亮度的浅色。
            var hue = ((index - 10) / 10) * 15.0;
            var tone = (index - 10) % 10;
            var brightnessLevels = new[] { 255, 255, 165, 165, 127, 127, 76, 76, 38, 38 };
            var saturation = tone % 2 == 0 ? 1.0 : 0.5;
            return ColorFromHsv(hue, saturation, brightnessLevels[tone] / 255.0);
        }

        var grays = new[] { 51, 80, 105, 130, 190, 255 };
        if (index >= 250 && index <= 255)
        {
            var gray = grays[index - 250];
            return Color.FromArgb(gray, gray, gray);
        }

        return Color.DimGray;
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        var sector = hue / 60.0;
        var wholeSector = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (red, green, blue) = wholeSector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return Color.FromArgb(
            (int)Math.Round(red * 255),
            (int)Math.Round(green * 255),
            (int)Math.Round(blue * 255));
    }

    private sealed class DirectoryColorItem
    {
        public int Index { get; }
        public Color Color { get; }

        public DirectoryColorItem(int index, Color color)
        {
            Index = index;
            Color = color;
        }
    }

    private void ConfigureDirectoryColumnsGrid()
    {
        UiLayout.StyleGrid(_directoryColumnsGrid, Font);
        _directoryColumnsGrid.MultiSelect = false;
        _directoryColumnsGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _directoryColumnsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _directoryColumnsGrid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        _directoryColumnsGrid.CellContentClick += DirectoryColumnsGridCellContentClick;
        _directoryColumnsGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_directoryColumnsGrid.IsCurrentCellDirty)
            {
                _directoryColumnsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _directoryColumnsGrid.CellValueChanged += (_, _) => UpdateDirectoryPreview();
        _directoryColumnsGrid.CellEndEdit += (_, _) => UpdateDirectoryPreview();
        _directoryColumnsGrid.DataError += (_, _) => { };

        _directoryColumnsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Enabled",
            HeaderText = "是否启用",
            Width = UiLayout.Scale(70)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Centered",
            HeaderText = "文字居中",
            Width = UiLayout.Scale(70)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Header",
            HeaderText = "目录列名",
            ReadOnly = true,
            Width = UiLayout.Scale(105)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Width",
            HeaderText = "目录列宽",
            Width = UiLayout.Scale(92)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "PickWidth",
            HeaderText = "设置列宽",
            Text = "图中交互",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(88)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "MoveUp",
            HeaderText = "上移",
            Text = "上移",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(56)
        });
        _directoryColumnsGrid.Columns.Add(new DataGridViewButtonColumn
        {
            Name = "MoveDown",
            HeaderText = "下移",
            Text = "下移",
            UseColumnTextForButtonValue = true,
            Width = UiLayout.Scale(56)
        });

        // 列顺序只允许通过“上移/下移”按钮改变，禁止点击表头触发隐式排序。
        foreach (DataGridViewColumn column in _directoryColumnsGrid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
        }
    }

    private void DirectoryColumnsGridCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
        {
            return;
        }

        var columnName = _directoryColumnsGrid.Columns[e.ColumnIndex].Name;
        if (columnName == "PickWidth")
        {
            RequestColumnWidthFromCad(e.RowIndex);
        }
        else if (columnName == "MoveUp")
        {
            MoveDirectoryColumn(e.RowIndex, -1);
        }
        else if (columnName == "MoveDown")
        {
            MoveDirectoryColumn(e.RowIndex, 1);
        }
    }

    private void MoveDirectoryColumn(int rowIndex, int offset)
    {
        var targetIndex = rowIndex + offset;
        if (rowIndex < 0 || targetIndex < 0 || targetIndex >= _directoryColumnsGrid.Rows.Count)
        {
            return;
        }

        // 直接移动整行可同时保留字段键、启用状态、对齐方式、固定列名和用户输入的列宽。
        var row = _directoryColumnsGrid.Rows[rowIndex];
        _directoryColumnsGrid.Rows.RemoveAt(rowIndex);
        _directoryColumnsGrid.Rows.Insert(targetIndex, row);
        _directoryColumnsGrid.ClearSelection();
        row.Selected = true;
        _directoryColumnsGrid.CurrentCell = row.Cells[2];
        UpdateDirectoryPreview();
    }

    private static TableLayoutPanel CreateSettingsTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            Padding = new Padding(UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(145)));
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
        SelectFileNameSeparator(settings.PdfFileNameSeparator);
        LoadFileNameFields(settings.PdfFileNameFields);
        _fileNameSequenceDigits.Value = Math.Max(1, Math.Min(10, settings.FileNameSequenceDigits));
        _openExternalDwgForPlot.Checked = settings.OpenExternalDwgForPlot;
        _directoryColorIndex.SelectedIndex = Math.Max(0, Math.Min(256, settings.DirectoryColorIndex));
        _directoryTextHeight.Value = UiLayout.Clamp(_directoryTextHeight, settings.DirectoryTextHeight);
        _directoryTextWidthFactor.Value = UiLayout.Clamp(_directoryTextWidthFactor, settings.DirectoryTextWidthFactor);
        _directoryRowHeight.Value = UiLayout.Clamp(_directoryRowHeight, settings.DirectoryRowHeight);
        _directoryLayerName.Text = settings.DirectoryLayerName;
        _directoryDrawHeader.Checked = settings.DirectoryDrawHeader;
        _directoryDrawGridLines.Checked = settings.DirectoryDrawGridLines;
        SelectTextStyle(settings.DirectoryTextStyleName);
        LoadDirectoryColumns(settings.DirectoryColumns);
    }

    private void SaveSettings()
    {
        if (!TryReadSettingsFromControls(out var current))
        {
            return;
        }

        AppSettingsStore.Save(current);
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryReadSettingsFromControls(out AppSettings current)
    {
        current = AppSettingsStore.Load();
        if (!TryReadDirectoryColumns(out var directoryColumns))
        {
            return false;
        }

        current.RememberLastOutputDirectory = _rememberOutput.Checked;
        current.DefaultOutputSubfolder = string.IsNullOrWhiteSpace(_outputSubfolder.Text) ? "PDF" : _outputSubfolder.Text.Trim();
        current.AutoScanCurrentDrawing = false;
        current.PaperMatchToleranceMm = (double)_paperTolerance.Value;
        current.AllowStandardPaperNameFallback = _allowPaperNameFallback.Checked;
        current.ShowPlotProgress = _showProgress.Checked;
        current.AddSequenceWhenPdfExists = _addSequenceWhenPdfExists.Checked;
        current.PdfFileNameSeparator = AppSettingsStore.NormalizeFileNameSeparator(SelectedFileNameSeparator());
        current.PdfFileNameFields = _selectedFields.Items
            .Cast<FileNameFieldItem>()
            .Select(x => x.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (current.PdfFileNameFields.Count == 0)
        {
            MessageBox.Show("文件名至少需要一个字段。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        current.FileNameSequenceDigits = (int)_fileNameSequenceDigits.Value;
        current.OpenExternalDwgForPlot = _openExternalDwgForPlot.Checked;
        current.DirectoryColorIndex = _directoryColorIndex.SelectedItem is DirectoryColorItem colorItem
            ? colorItem.Index
            : 7;
        current.DirectoryTextHeight = (double)_directoryTextHeight.Value;
        current.DirectoryTextWidthFactor = (double)_directoryTextWidthFactor.Value;
        current.DirectoryRowHeight = (double)_directoryRowHeight.Value;
        current.DirectoryTextHeightRatio = Math.Max(0.01, Math.Min(0.9, current.DirectoryTextHeight / current.DirectoryRowHeight));
        current.DirectoryTextStyleName = _directoryTextStyle.SelectedItem?.ToString() == DefaultTextStyleDisplay
            ? ""
            : _directoryTextStyle.SelectedItem?.ToString() ?? "";
        current.DirectoryLayerName = string.IsNullOrWhiteSpace(_directoryLayerName.Text) ? "0" : _directoryLayerName.Text.Trim();
        current.DirectoryDrawHeader = _directoryDrawHeader.Checked;
        current.DirectoryDrawGridLines = _directoryDrawGridLines.Checked;
        current.DirectoryColumns = directoryColumns;
        return true;
    }

    private void LoadDirectoryColumns(IEnumerable<DirectoryColumnSetting> columns)
    {
        _directoryColumnsGrid.Rows.Clear();
        foreach (var column in columns)
        {
            var rowIndex = _directoryColumnsGrid.Rows.Add(
                column.Enabled,
                column.Centered,
                column.Header,
                column.Width.ToString("0.##", CultureInfo.CurrentCulture));
            _directoryColumnsGrid.Rows[rowIndex].Tag = column.Key;
        }
        UpdateDirectoryPreview();
    }

    private void UpdateDirectoryPreview()
    {
        if (_directoryColumnsGrid.Columns.Count == 0)
        {
            return;
        }

        var columns = new List<DirectoryPreviewColumn>();
        foreach (DataGridViewRow row in _directoryColumnsGrid.Rows)
        {
            if (!Convert.ToBoolean(row.Cells["Enabled"].Value ?? false))
            {
                continue;
            }

            var widthText = row.Cells["Width"].Value?.ToString() ?? "";
            if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.CurrentCulture, out var width)
                && !double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            {
                continue;
            }
            if (width <= 0)
            {
                continue;
            }

            columns.Add(new DirectoryPreviewColumn(
                row.Cells["Header"].Value?.ToString() ?? "",
                width,
                Convert.ToBoolean(row.Cells["Centered"].Value ?? false)));
        }

        var styleName = _directoryTextStyle.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(styleName) || styleName == DefaultTextStyleDisplay)
        {
            styleName = Font.Name;
        }
        _directoryOrderPreview.SetPreview(
            columns,
            (double)_directoryRowHeight.Value,
            (double)_directoryTextHeight.Value,
            (double)_directoryTextWidthFactor.Value,
            styleName ?? Font.Name);
    }

    private sealed class DirectoryPreviewColumn
    {
        public string Header { get; }
        public double Width { get; }
        public bool Centered { get; }

        public DirectoryPreviewColumn(string header, double width, bool centered)
        {
            Header = header;
            Width = width;
            Centered = centered;
        }
    }

    private sealed class DirectoryPreviewControl : Control
    {
        private IReadOnlyList<DirectoryPreviewColumn> _columns = Array.Empty<DirectoryPreviewColumn>();
        private double _rowHeight = 1;
        private double _textHeight = 1;
        private double _textWidthFactor = 0.7;
        private string _fontName = "宋体";

        public DirectoryPreviewControl()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            Margin = Padding.Empty;
        }

        public void SetPreview(
            IReadOnlyList<DirectoryPreviewColumn> columns,
            double rowHeight,
            double textHeight,
            double textWidthFactor,
            string fontName)
        {
            _columns = columns.ToList();
            _rowHeight = Math.Max(1, rowHeight);
            _textHeight = Math.Max(1, textHeight);
            _textWidthFactor = Math.Max(0.1, textWidthFactor);
            _fontName = string.IsNullOrWhiteSpace(fontName) ? "宋体" : fontName;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            if (_columns.Count == 0)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    "请勾选需要生成的目录列",
                    Font,
                    ClientRectangle,
                    Color.DimGray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            var totalWidth = _columns.Sum(x => x.Width);
            if (totalWidth <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            var padding = UiLayout.Scale(6);
            var availableWidth = Math.Max(1, ClientSize.Width - padding * 2);
            var availableHeight = Math.Max(1, ClientSize.Height - padding * 2);
            // 列宽和行高共用同一个缩放比例，保证预览中的长宽关系与最终 CAD 目录完全一致。
            var scale = Math.Min(availableWidth / totalWidth, availableHeight / _rowHeight);
            var previewWidth = (float)(totalWidth * scale);
            var previewHeight = (float)(_rowHeight * scale);
            var x = (ClientSize.Width - previewWidth) / 2f;
            var y = (ClientSize.Height - previewHeight) / 2f;

            using var linePen = new Pen(Color.FromArgb(70, 70, 70), Math.Max(1, UiLayout.Scale(1)));
            foreach (var column in _columns)
            {
                var cellWidth = (float)(column.Width * scale);
                var cell = new RectangleF(x, y, cellWidth, previewHeight);
                e.Graphics.DrawRectangle(linePen, cell.X, cell.Y, cell.Width, cell.Height);

                // 与目录生成逻辑保持相同的行高和列宽限幅，预览字高即最终实际可用字高的等比结果。
                var byRow = _rowHeight * 0.8;
                var byWidth = column.Width * 0.9 / Math.Max(1, column.Header.Length * _textWidthFactor);
                var fontPixels = (float)(Math.Max(1, Math.Min(_textHeight, Math.Min(byRow, byWidth))) * scale);
                using var previewFont = CreatePreviewFont(_fontName, Math.Max(1, fontPixels), Font);
                using var format = new StringFormat
                {
                    Alignment = column.Centered ? StringAlignment.Center : StringAlignment.Near,
                    LineAlignment = StringAlignment.Center,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                var textCell = RectangleF.Inflate(cell, -Math.Min(UiLayout.Scale(4), cell.Width * 0.04f), 0);
                e.Graphics.DrawString(column.Header, previewFont, Brushes.Black, textCell, format);
                x += cellWidth;
            }
        }

        private static System.Drawing.Font CreatePreviewFont(string fontName, float size, System.Drawing.Font fallback)
        {
            try
            {
                return new System.Drawing.Font(fontName, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                return new System.Drawing.Font(fallback.FontFamily, size, FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }
    }

    private bool TryReadDirectoryColumns(out List<DirectoryColumnSetting> columns)
    {
        columns = new List<DirectoryColumnSetting>();
        _directoryColumnsGrid.EndEdit();
        foreach (DataGridViewRow row in _directoryColumnsGrid.Rows)
        {
            var key = row.Tag?.ToString() ?? "";
            var header = row.Cells["Header"].Value?.ToString()?.Trim() ?? "";
            var widthText = row.Cells["Width"].Value?.ToString()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(header))
            {
                MessageBox.Show("目录列名不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _directoryColumnsGrid.CurrentCell = row.Cells["Header"];
                return false;
            }

            if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.CurrentCulture, out var width)
                && !double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            {
                width = 0;
            }
            if (width <= 0)
            {
                MessageBox.Show($"目录列“{header}”的列宽必须大于 0。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _directoryColumnsGrid.CurrentCell = row.Cells["Width"];
                return false;
            }

            columns.Add(new DirectoryColumnSetting
            {
                Key = key,
                Header = header,
                Enabled = Convert.ToBoolean(row.Cells["Enabled"].Value ?? false),
                Centered = Convert.ToBoolean(row.Cells["Centered"].Value ?? false),
                Width = width
            });
        }

        if (!columns.Any(x => x.Enabled))
        {
            MessageBox.Show("请至少启用一个目录字段。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
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

        // 即使当前图纸尚未建立“宋体”文字样式，也先在界面提供该默认项；生成目录时会在本图内自动创建。
        if (!_directoryTextStyle.Items.Cast<object>().Any(x =>
            string.Equals(x?.ToString(), "宋体", StringComparison.OrdinalIgnoreCase)))
        {
            _directoryTextStyle.Items.Insert(1, "宋体");
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

    private void RequestColumnWidthFromCad(int rowIndex)
    {
        if (_document == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (rowIndex < 0 || rowIndex >= _directoryColumnsGrid.Rows.Count
            || !TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        var key = _directoryColumnsGrid.Rows[rowIndex].Tag?.ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        // CAD 取点必须在设置窗体关闭后执行；先保存全部未提交编辑，再由调用方回到命令上下文框选。
        AppSettingsStore.Save(settings);
        RequestedDirectoryColumnKey = key;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RequestRowHeightFromCad()
    {
        if (_document == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 与列宽交互一致，先保存当前页面编辑，再关闭模态窗体回到 CAD 命令上下文量取高度。
        AppSettingsStore.Save(settings);
        RequestPickDirectoryRowHeight = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SelectedTabIndex = _tabs.SelectedIndex;
        InitialTabIndex = SelectedTabIndex;
        base.OnFormClosing(e);
    }

    private void ResetDefaults()
    {
        Apply(AppSettingsStore.Default());
    }
}
