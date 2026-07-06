using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

public sealed class SinglePlotPaperSelectionForm : Form
{
    private readonly ComboBox _choices = new();
    private readonly IReadOnlyList<PaperDetection> _candidates;

    public PaperDetection SelectedPaper =>
        _choices.SelectedIndex >= 0 && _choices.SelectedIndex < _candidates.Count
            ? _candidates[_choices.SelectedIndex]
            : _candidates[0];

    public SinglePlotPaperSelectionForm(IReadOnlyList<PaperDetection> candidates)
    {
        _candidates = candidates.Count > 0
            ? candidates
            : throw new ArgumentException("至少需要一个纸张候选项。", nameof(candidates));
        InitializeComponents();
    }

    private void InitializeComponents()
    {
        Text = "选择纸张";
        UiLayout.ConfigureForm(this, 560, 210, 560, 210);
        // 高 DPI 下 Form.Size 会包含标题栏和边框，显式设置紧凑 ClientSize，避免内容重叠又不过度占屏。
        ClientSize = new Size(UiLayout.Scale(560), UiLayout.Scale(180));
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(14))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(50)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(new Label
        {
            Text = "该外框可匹配多种纸张和比例，\r\n请选择本次打印使用的纸张：",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _choices.Dock = DockStyle.Fill;
        _choices.DropDownStyle = ComboBoxStyle.DropDownList;
        _choices.Items.AddRange(_candidates
            .Select(candidate =>
                $"{candidate.PaperName}    {candidate.ScaleText}    {candidate.PaperWidthMm:0.##} × {candidate.PaperHeightMm:0.##} mm")
            .Cast<object>()
            .ToArray());
        _choices.SelectedIndex = 0;
        root.Controls.Add(_choices, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = UiLayout.Scale(36),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, UiLayout.Scale(4), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 82);
        ok.DialogResult = DialogResult.OK;
        var cancel = UiLayout.CreateButton("取消", 82);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(root);
    }
}
