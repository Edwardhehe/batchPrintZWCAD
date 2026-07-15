using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

/// <summary>
/// 自定义打印比例对话框。
/// 当图纸尺寸无法匹配标准 A0-A4 纸张时弹出，让用户选择整数打印比例。
/// </summary>
public sealed class CustomScaleForm : Form
{
    private readonly NumericUpDown _scaleInput;
    private readonly Label _paperSizeLabel;
    private readonly double _drawingWidth;
    private readonly double _drawingHeight;

    public int SelectedScale => (int)_scaleInput.Value;
    public double PaperWidthMm => _drawingWidth / SelectedScale;
    public double PaperHeightMm => _drawingHeight / SelectedScale;

    public CustomScaleForm(double drawingWidth, double drawingHeight, int guessedScale)
    {
        _drawingWidth = drawingWidth;
        _drawingHeight = drawingHeight;

        Text = "自定义打印比例";
        UiLayout.ConfigureForm(this, 360, 180, 340, 170);
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        ClientSize = new Size(UiLayout.Scale(360), UiLayout.Scale(170));

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(22)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));

        var info = new Label
        {
            Text = $"图纸尺寸: {drawingWidth:0.##} x {drawingHeight:0.##} mm（未匹配到 A0-A4 标准纸张）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };

        var scaleLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty };
        var scaleLabel = new Label { Text = "打印比例  1 : ", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        _scaleInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Value = guessedScale > 0 ? guessedScale : 100,
            Width = UiLayout.Scale(80),
            DecimalPlaces = 0
        };
        _scaleInput.ValueChanged += (_, _) => UpdatePaperSize();
        scaleLayout.Controls.Add(scaleLabel);
        scaleLayout.Controls.Add(_scaleInput);

        _paperSizeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(0, 120, 212)
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        var cancel = UiLayout.CreateButton("取消", 76);
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        panel.Controls.Add(info, 0, 0);
        panel.Controls.Add(scaleLayout, 0, 1);
        panel.Controls.Add(_paperSizeLabel, 0, 2);
        panel.Controls.Add(new Label(), 0, 3); // spacer
        panel.Controls.Add(buttons, 0, 4);
        Controls.Add(panel);

        AcceptButton = ok;
        CancelButton = cancel;

        UpdatePaperSize();
    }

    private void UpdatePaperSize()
    {
        var pw = PaperWidthMm;
        var ph = PaperHeightMm;
        _paperSizeLabel.Text = $"打印纸张: {pw:0.##} x {ph:0.##} mm（自定义尺寸）";
    }
}
