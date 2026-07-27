using System;
using System.Drawing;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
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
    private readonly ComboBox _pdfFileNameSeparator = new();
    private readonly CheckBox _openExternalDwgForPlot = new();
    private readonly ComboBox _defaultDevice = new();
    private readonly NumericUpDown _directoryIndexWidth = new();
    private readonly NumericUpDown _directoryNumberWidth = new();
    private readonly NumericUpDown _directoryTitleWidth = new();
    private readonly NumericUpDown _directoryPaperWidth = new();
    private readonly NumericUpDown _directoryRemarkWidth = new();
    private readonly NumericUpDown _directoryRowHeight = new();
    private readonly NumericUpDown _directoryTextRatio = new();
    private readonly ComboBox _directoryTextStyle = new();

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
        UiLayout.ConfigureForm(this, 760, 690, 680, 560);
        MaximizeBox = false;
        MinimizeBox = false;

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
        var table = CreateSettingsTable(10);

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

        _pdfFileNameSeparator.DropDownStyle = ComboBoxStyle.DropDownList;
        _pdfFileNameSeparator.Dock = DockStyle.Left;
        _pdfFileNameSeparator.Width = UiLayout.Scale(220);
        _pdfFileNameSeparator.Items.AddRange(new object[]
        {
            "_  下划线",
            "空格",
            "-  短横线",
            " -  前后带空格",
            "无"
        });

        _openExternalDwgForPlot.Text = "跨文件打印时临时打开 DWG";
        _openExternalDwgForPlot.AutoSize = true;
        _openExternalDwgForPlot.Dock = DockStyle.Fill;

        AddRow(table, 0, "", _rememberOutput);
        AddRow(table, 1, "默认输出子文件夹", _outputSubfolder);

        _defaultDevice.DropDownStyle = ComboBoxStyle.DropDownList;
        _defaultDevice.Dock = DockStyle.Left;
        _defaultDevice.Width = UiLayout.Scale(280);
        LoadPlotDevices();
        AddRow(table, 2, "默认打印机", _defaultDevice);

        AddRow(table, 3, "", _autoScan);
        AddRow(table, 4, "纸张匹配容差(mm)", _paperTolerance);
        AddRow(table, 5, "", _allowPaperNameFallback);
        AddRow(table, 6, "", _showProgress);
        AddRow(table, 7, "", _addSequenceWhenPdfExists);
        AddRow(table, 8, "PDF文件名连接符", _pdfFileNameSeparator);
        AddRow(table, 9, "", _openExternalDwgForPlot);
        page.Controls.Add(table);
        return page;
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

        AddRow(table, 0, "序号列宽", _directoryIndexWidth);
        AddRow(table, 1, "图号列宽", _directoryNumberWidth);
        AddRow(table, 2, "图名列宽", _directoryTitleWidth);
        AddRow(table, 3, "图幅列宽", _directoryPaperWidth);
        AddRow(table, 4, "备注列宽", _directoryRemarkWidth);
        AddRow(table, 5, "行高", _directoryRowHeight);
        AddRow(table, 6, "文字高度比例", _directoryTextRatio);
        AddRow(table, 7, "目录文字样式", _directoryTextStyle);
        AddRow(table, 8, "", pickButton);
        AddRow(table, 9, "", new Label
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

    private static void AddRow(TableLayoutPanel table, int row, string labelText, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        table.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, row);
        table.Controls.Add(control, 1, row);
    }

#if AUTOCAD
    private void LoadPlotDevices()
    {
        _defaultDevice.Items.Clear();
        _defaultDevice.Items.Add("(不强制指定)");
        try
        {
            foreach (var item in PlotSettingsValidator.Current.GetPlotDeviceList())
            {
                if (item is string device && !string.IsNullOrWhiteSpace(device))
                    _defaultDevice.Items.Add(device);
            }
        }
        catch { }
        _defaultDevice.SelectedIndex = 0;
    }
#else
    private void LoadPlotDevices()
    {
        _defaultDevice.Items.Add("(不可用)");
        _defaultDevice.Enabled = false;
    }
#endif

    private void LoadSettings()
    {
        Apply(AppSettingsStore.Load());
    }

    private void Apply(AppSettings settings)
    {
        _rememberOutput.Checked = settings.RememberLastOutputDirectory;
        _outputSubfolder.Text = settings.DefaultOutputSubfolder;
        _autoScan.Checked = false;
        _paperTolerance.Value = Clamp(_paperTolerance, settings.PaperMatchToleranceMm);
        _allowPaperNameFallback.Checked = settings.AllowStandardPaperNameFallback;
        _showProgress.Checked = settings.ShowPlotProgress;
        _addSequenceWhenPdfExists.Checked = settings.AddSequenceWhenPdfExists;
        SelectPdfFileNameSeparator(settings.PdfFileNameSeparator);
        _openExternalDwgForPlot.Checked = settings.OpenExternalDwgForPlot;
        SelectComboValue(_defaultDevice, settings.DefaultPlotDevice);
        _directoryIndexWidth.Value = Clamp(_directoryIndexWidth, settings.DirectoryIndexWidth);
        _directoryNumberWidth.Value = Clamp(_directoryNumberWidth, settings.DirectoryNumberWidth);
        _directoryTitleWidth.Value = Clamp(_directoryTitleWidth, settings.DirectoryTitleWidth);
        _directoryPaperWidth.Value = Clamp(_directoryPaperWidth, settings.DirectoryPaperWidth);
        _directoryRemarkWidth.Value = Clamp(_directoryRemarkWidth, settings.DirectoryRemarkWidth);
        _directoryRowHeight.Value = Clamp(_directoryRowHeight, settings.DirectoryRowHeight);
        _directoryTextRatio.Value = Clamp(_directoryTextRatio, settings.DirectoryTextHeightRatio);
        SelectTextStyle(settings.DirectoryTextStyleName);
    }

    private static decimal Clamp(NumericUpDown input, double value)
    {
        return (decimal)Math.Max((double)input.Minimum, Math.Min((double)input.Maximum, value));
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
        current.PdfFileNameSeparator = ReadPdfFileNameSeparator();
        current.DefaultPlotDevice = _defaultDevice.SelectedItem?.ToString() == "(不强制指定)" ? "" : _defaultDevice.SelectedItem?.ToString() ?? "";
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

    private string ReadPdfFileNameSeparator()
    {
        return _pdfFileNameSeparator.SelectedItem?.ToString() switch
        {
            "空格" => " ",
            "-  短横线" => "-",
            " -  前后带空格" => " - ",
            "无" => "",
            _ => "_"
        };
    }

    private void SelectPdfFileNameSeparator(string? separator)
    {
        var item = separator switch
        {
            " " => "空格",
            "-" => "-  短横线",
            " - " => " -  前后带空格",
            "" => "无",
            _ => "_  下划线"
        };
        _pdfFileNameSeparator.SelectedItem = item;
        if (_pdfFileNameSeparator.SelectedIndex < 0 && _pdfFileNameSeparator.Items.Count > 0)
        {
            _pdfFileNameSeparator.SelectedIndex = 0;
        }
    }

    private static void SelectComboValue(ComboBox combo, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { combo.SelectedIndex = 0; return; }
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = 0;
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
