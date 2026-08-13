using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

/// <summary>
/// 自定义打印比例对话框。
/// 当图纸尺寸无法匹配标准 A0-A4 纸张时弹出，让用户输入绘图比例（支持小数，如 143 / 1:143 / 0.25 表示 4:1）。
/// 比例语义：图面尺寸 / 比例 = 纸张毫米尺寸。本对话框仅用于缩小场景，比例必须 ≥ 1。
/// </summary>
public sealed class CustomScaleForm : Form
{
    // 换算后纸张短边的常规范围（mm），与 PaperSizeDetector.GuessScale 的判断范围一致。
    private const double PaperShortSideMinMm = 100d;
    private const double PaperShortSideMaxMm = 900d;

    private readonly TextBox _scaleInput = new();
    private readonly Label _paperSizeLabel;
    private readonly double _drawingWidth;
    private readonly double _drawingHeight;

    public double SelectedScale { get; private set; }
    public double PaperWidthMm => _drawingWidth / SelectedScale;
    public double PaperHeightMm => _drawingHeight / SelectedScale;

    public CustomScaleForm(double drawingWidth, double drawingHeight, int guessedScale, string? hintText = null)
    {
        _drawingWidth = drawingWidth;
        _drawingHeight = drawingHeight;

        Text = "自定义打印比例";
        UiLayout.ConfigureForm(this, 360, 180, 340, 170);
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        ClientSize = new Size(UiLayout.Scale(360), UiLayout.Scale(190));

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(22)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(26)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // hint 行（无提示文案时留空）
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(14)));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));

        var info = new Label
        {
            Text = $"图纸尺寸: {drawingWidth:0.##} x {drawingHeight:0.##} mm（未匹配到 A0-A4 标准纸张）",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray
        };

        // 输入框只放比例数字部分（如 100 表示 1:100），"1 :" 前缀由标签固定显示；
        // 用户也可直接粘贴 "1:143" 完整写法，解析兼容两种形式。
        var scaleLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty };
        var scaleLabel = new Label { Text = "打印比例  1 : ", AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        _scaleInput.Width = UiLayout.Scale(140);
        _scaleInput.Text = guessedScale > 0 ? guessedScale.ToString() : "100";
        _scaleInput.TextChanged += (_, _) => UpdatePaperSize();
        scaleLayout.Controls.Add(scaleLabel);
        scaleLayout.Controls.Add(_scaleInput);

        _paperSizeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(0, 120, 212)
        };

        // 提示文案：说明该比例仅用于把图幅转化为接近常规纸张大小的图纸，避免打印出超大尺寸的图。
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = string.IsNullOrWhiteSpace(hintText) ? "" : hintText
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(6), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        var cancel = UiLayout.CreateButton("取消", 76);
        ok.Click += (_, _) => Confirm();
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        panel.Controls.Add(info, 0, 0);
        panel.Controls.Add(scaleLayout, 0, 1);
        panel.Controls.Add(_paperSizeLabel, 0, 2);
        panel.Controls.Add(hint, 0, 3);
        panel.Controls.Add(new Label(), 0, 4); // spacer
        panel.Controls.Add(buttons, 0, 5);
        Controls.Add(panel);

        AcceptButton = ok;
        CancelButton = cancel;

        UpdatePaperSize();
    }

    private void UpdatePaperSize()
    {
        // 输入无效或比例 <1 时只显示输入提示，不显示纸张尺寸。
        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale) || scale < 1)
        {
            _paperSizeLabel.Text = "请输入 ≥1 的比例数字，例如 100";
            return;
        }

        var pw = _drawingWidth / scale;
        var ph = _drawingHeight / scale;
        var shortSide = Math.Min(pw, ph);
        var rangeText = shortSide >= PaperShortSideMinMm && shortSide <= PaperShortSideMaxMm
            ? ""
            : "（超出常规 A4~A0 范围，可能造成超大或过小页面）";
        _paperSizeLabel.Text = $"打印纸张: {pw:0.##} x {ph:0.##} mm（自定义尺寸）{rangeText}";
        _paperSizeLabel.ForeColor = rangeText.Length == 0 ? Color.FromArgb(0, 120, 212) : Color.OrangeRed;
    }

    private void Confirm()
    {
        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale) || scale < 1)
        {
            MessageBox.Show(
                "无法识别比例输入。请直接输入数字（100 表示 1:100），也支持 1:143 形式；比例必须大于等于 1。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        // 换算纸张短边超出常规范围时二次确认，防止误输入导致超大纸张或内容缩成针尖（打印表现为空白页）。
        var paperWidthMm = _drawingWidth / scale;
        var paperHeightMm = _drawingHeight / scale;
        var shortSideMm = Math.Min(paperWidthMm, paperHeightMm);
        if (shortSideMm < PaperShortSideMinMm || shortSideMm > PaperShortSideMaxMm)
        {
            var confirm = MessageBox.Show(
                $"按 1:{PaperSizeDetector.ToScaleText(scale)} 换算的纸张为 {paperWidthMm:0.##} x {paperHeightMm:0.##} mm，"
                + "超出常规 A4~A0 图纸范围（短边 100~900mm），打印可能出现超大或过小页面。是否仍要使用该比例？",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes)
            {
                _scaleInput.Focus();
                _scaleInput.SelectAll();
                return;
            }
        }

        SelectedScale = scale;
        DialogResult = DialogResult.OK;
        Close();
    }
}
