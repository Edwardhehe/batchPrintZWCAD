using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public sealed class SettingsForm : Form
{
    private readonly CheckBox _rememberOutput = new();
    private readonly TextBox _outputSubfolder = new();
    private readonly CheckBox _autoScan = new();
    private readonly NumericUpDown _paperTolerance = new();
    private readonly CheckBox _allowPaperNameFallback = new();
    private readonly CheckBox _showProgress = new();
    private readonly CheckBox _addSequenceWhenPdfExists = new();
    private readonly CheckBox _openExternalDwgForPlot = new();

    public SettingsForm()
    {
        InitializeComponents();
        LoadSettings();
    }

    private void InitializeComponents()
    {
        Text = "批量打印设置";
        UiLayout.ConfigureForm(this, 700, 470, 620, 420);
        MaximizeBox = false;
        MinimizeBox = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(14)),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));

        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(190)));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _rememberOutput.Text = "记住上次输出目录";
        _rememberOutput.AutoSize = true;
        _rememberOutput.Dock = DockStyle.Fill;

        _outputSubfolder.Dock = DockStyle.Fill;

        _autoScan.Text = "打开批量打印窗口时自动扫描当前图";
        _autoScan.AutoSize = true;
        _autoScan.Dock = DockStyle.Fill;

        _paperTolerance.DecimalPlaces = 1;
        _paperTolerance.Minimum = 0.5M;
        _paperTolerance.Maximum = 20M;
        _paperTolerance.Increment = 0.5M;
        _paperTolerance.Dock = DockStyle.Left;
        _paperTolerance.Width = UiLayout.Scale(100);

        _allowPaperNameFallback.Text = "尺寸匹配失败时允许按 A0/A1/A2/A3 名称兜底";
        _allowPaperNameFallback.AutoSize = true;
        _allowPaperNameFallback.Dock = DockStyle.Fill;

        _showProgress.Text = "打印时显示 CAD 打印进度窗口";
        _showProgress.AutoSize = true;
        _showProgress.Dock = DockStyle.Fill;

        _addSequenceWhenPdfExists.Text = "PDF 已存在时自动加序号（不勾选则覆盖）";
        _addSequenceWhenPdfExists.AutoSize = true;
        _addSequenceWhenPdfExists.Dock = DockStyle.Fill;

        _openExternalDwgForPlot.Text = "跨文件打印时临时打开 DWG（保证视口和外部参照正常）";
        _openExternalDwgForPlot.AutoSize = true;
        _openExternalDwgForPlot.Dock = DockStyle.Fill;

        AddRow(settings, 0, "", _rememberOutput);
        AddRow(settings, 1, "默认输出子文件夹", _outputSubfolder);
        AddRow(settings, 2, "", _autoScan);
        AddRow(settings, 3, "纸张匹配容差(mm)", _paperTolerance);
        AddRow(settings, 4, "", _allowPaperNameFallback);
        AddRow(settings, 5, "", _showProgress);
        AddRow(settings, 6, "", _addSequenceWhenPdfExists);
        AddRow(settings, 7, "", _openExternalDwgForPlot);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "默认会覆盖已存在的同名 PDF；跨文件打印默认临时打开 DWG，以保证布局视口和外部参照加载。"
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
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

        root.Controls.Add(settings, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
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

    private void LoadSettings()
    {
        Apply(AppSettingsStore.Load());
    }

    private void Apply(AppSettings settings)
    {
        _rememberOutput.Checked = settings.RememberLastOutputDirectory;
        _outputSubfolder.Text = settings.DefaultOutputSubfolder;
        _autoScan.Checked = settings.AutoScanCurrentDrawing;
        _paperTolerance.Value = (decimal)Math.Max((double)_paperTolerance.Minimum, Math.Min((double)_paperTolerance.Maximum, settings.PaperMatchToleranceMm));
        _allowPaperNameFallback.Checked = settings.AllowStandardPaperNameFallback;
        _showProgress.Checked = settings.ShowPlotProgress;
        _addSequenceWhenPdfExists.Checked = settings.AddSequenceWhenPdfExists;
        _openExternalDwgForPlot.Checked = settings.OpenExternalDwgForPlot;
    }

    private void SaveSettings()
    {
        var current = AppSettingsStore.Load();
        current.RememberLastOutputDirectory = _rememberOutput.Checked;
        current.DefaultOutputSubfolder = string.IsNullOrWhiteSpace(_outputSubfolder.Text) ? "PDF" : _outputSubfolder.Text.Trim();
        current.AutoScanCurrentDrawing = _autoScan.Checked;
        current.PaperMatchToleranceMm = (double)_paperTolerance.Value;
        current.AllowStandardPaperNameFallback = _allowPaperNameFallback.Checked;
        current.ShowPlotProgress = _showProgress.Checked;
        current.AddSequenceWhenPdfExists = _addSequenceWhenPdfExists.Checked;
        current.OpenExternalDwgForPlot = _openExternalDwgForPlot.Checked;
        AppSettingsStore.Save(current);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetDefaults()
    {
        Apply(AppSettingsStore.Default());
    }
}
