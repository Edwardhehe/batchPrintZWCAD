using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public sealed class PaperSizeSelectionForm : Form
{
    private readonly ComboBox _paperName = new();
    private readonly NumericUpDown _width = new();
    private readonly NumericUpDown _height = new();

    public string PaperName => _paperName.Text.Trim();
    public double PaperWidthMm => (double)_width.Value;
    public double PaperHeightMm => (double)_height.Value;

    public PaperSizeSelectionForm(PaperDetection detected)
    {
        InitializeComponents();
        ApplyDetected(detected);
    }

    private void InitializeComponents()
    {
        Text = "设置图框输出纸张";
        UiLayout.ConfigureForm(this, 460, 230, 420, 210);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(14))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(110)));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _paperName.Dock = DockStyle.Fill;
        _paperName.DropDownStyle = ComboBoxStyle.DropDown;
        _paperName.Items.AddRange(new object[] { "A0", "A1", "A2", "A3", "A4", "A0+", "A1+", "A2+", "A3+", "自定义" });
        _paperName.SelectedIndexChanged += (_, _) => ApplyPreset(_paperName.Text);

        ConfigureNumber(_width);
        ConfigureNumber(_height);

        AddRow(fields, 0, "纸张", _paperName);
        AddRow(fields, 1, "宽度(mm)", _width);
        AddRow(fields, 2, "高度(mm)", _height);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "以后这个块名的图框都会按这里的纸张尺寸输出。"
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var ok = UiLayout.CreateButton("确定", 82);
        ok.Click += (_, _) => SaveAndClose();
        var cancel = UiLayout.CreateButton("取消", 82);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;

        root.Controls.Add(fields, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
    }

    private static void ConfigureNumber(NumericUpDown input)
    {
        input.DecimalPlaces = 2;
        input.Minimum = 1;
        input.Maximum = 5000;
        input.Increment = 1;
        input.Dock = DockStyle.Left;
        input.Width = UiLayout.Scale(120);
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

    private void ApplyDetected(PaperDetection detected)
    {
        _paperName.Text = string.IsNullOrWhiteSpace(detected.PaperName)
                || detected.PaperWidthMm <= 0
                || detected.PaperHeightMm <= 0
                || detected.PaperName.StartsWith("未", StringComparison.Ordinal)
            ? "自定义"
            : detected.PaperName;

        SetDimensions(
            detected.PaperWidthMm > 0 ? detected.PaperWidthMm : 420,
            detected.PaperHeightMm > 0 ? detected.PaperHeightMm : 297);
    }

    private void ApplyPreset(string paperName)
    {
        var (width, height) = PaperSizeDetector.GetDefaultSize(paperName.Trim(), PaperWidthMm, PaperHeightMm);
        if (width > 0 && height > 0)
        {
            SetDimensions(width, height);
        }
    }

    private void SetDimensions(double width, double height)
    {
        _width.Value = Clamp(width);
        _height.Value = Clamp(height);
    }

    private decimal Clamp(double value)
    {
        return (decimal)Math.Max((double)_width.Minimum, Math.Min((double)_width.Maximum, value));
    }

    private void SaveAndClose()
    {
        if (string.IsNullOrWhiteSpace(PaperName))
        {
            MessageBox.Show("纸张名称不能为空。", "设置图框输出纸张", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _paperName.Text = PaperName;
        _width.Text = PaperWidthMm.ToString("0.##", CultureInfo.InvariantCulture);
        _height.Text = PaperHeightMm.ToString("0.##", CultureInfo.InvariantCulture);
        DialogResult = DialogResult.OK;
        Close();
    }
}
