using System;
using System.Drawing;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

/// <summary>
/// 图号重排对话框 — 输入前缀、起始图号、排序方向，预览重排效果。
/// </summary>
public sealed class DrawingNumberReorderDialog : Form
{
    private readonly TextBox _prefix = new();
    private readonly TextBox _suffix = new();
    private readonly NumericUpDown _startNumber = new();
    private readonly ComboBox _sortOrder = new();
    private readonly Label _preview = new();
    private readonly int _jobCount;

    public string Prefix => _prefix.Text.Trim();
    public string Suffix => _suffix.Text.Trim();
    public int StartNumber => (int)_startNumber.Value;

    /// <summary>排序方向：先水平再垂直（从左到右，从上到下）</summary>
    public bool HorizontalFirst => _sortOrder.SelectedIndex == 1;

    /// <summary>用户点击"预览顺序"时触发，供父窗口临时更新编号和叠加层。</summary>
    public event Action? PreviewRequested;

    public DrawingNumberReorderDialog(int jobCount)
    {
        _jobCount = jobCount;

        Text = "图号重排";
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        UiLayout.ConfigureForm(this, 390, 290, 370, 270);
        ClientSize = new Size(UiLayout.Scale(390), UiLayout.Scale(270));

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(10), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(82)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 前缀
        _prefix.Text = "";
        _prefix.Dock = DockStyle.Left;
        _prefix.Width = UiLayout.Scale(160);
        AddRow(table, 0, "前缀", _prefix);

        // 后缀
        _suffix.Text = "";
        _suffix.Dock = DockStyle.Left;
        _suffix.Width = UiLayout.Scale(160);
        AddRow(table, 1, "后缀", _suffix);

        // 起始图号
        _startNumber.DecimalPlaces = 0;
        _startNumber.Minimum = 1;
        _startNumber.Maximum = 9999;
        _startNumber.Value = 1;
        _startNumber.Dock = DockStyle.Left;
        _startNumber.Width = UiLayout.Scale(100);
        AddRow(table, 2, "起始图号", _startNumber);

        // 排序方向
        _sortOrder.DropDownStyle = ComboBoxStyle.DropDownList;
        _sortOrder.Dock = DockStyle.Left;
        _sortOrder.Width = UiLayout.Scale(220);
        _sortOrder.Items.AddRange(new object[] { "从上到下，从左到右", "从左到右，从上到下" });
        _sortOrder.SelectedIndex = 0;
        _sortOrder.SelectedIndexChanged += (_, _) => UpdatePreview();
        AddRow(table, 3, "排序方向", _sortOrder);

        // 预览
        _preview.Dock = DockStyle.Fill;
        _preview.TextAlign = ContentAlignment.MiddleLeft;
        _preview.ForeColor = Color.DimGray;
        _preview.Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 7));
        table.SetColumnSpan(_preview, 2);
        table.Controls.Add(_preview, 0, 4);

        // 按钮
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(8), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var skip = UiLayout.CreateButton("预览顺序", 88);
        skip.Click += (_, _) => PreviewRequested?.Invoke();
        var cancel = UiLayout.CreateButton("取消", 76);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(skip);
        buttons.Controls.Add(cancel);
        table.SetColumnSpan(buttons, 2);
        table.Controls.Add(buttons, 0, 5);

        table.RowCount = 6;
        for (var i = 0; i < 5; i++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(32)));

        Controls.Add(table);
        _prefix.TextChanged += (_, _) => UpdatePreview();
        _suffix.TextChanged += (_, _) => UpdatePreview();
        _startNumber.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var prefix = Prefix;
        var suffix = Suffix;
        var start = StartNumber;
        var digits = Math.Max(2, (_jobCount + start - 1).ToString().Length);
        var samples = Math.Min(5, _jobCount);
        var items = new string[samples];
        for (var i = 0; i < samples; i++)
        {
            items[i] = prefix + (start + i).ToString($"D{digits}") + suffix;
        }

        var tail = _jobCount > samples ? " ..." : "";
        _preview.Text = $"示例: {string.Join(", ", items)}{tail}  (共{_jobCount}张)";
    }

    private static void AddRow(TableLayoutPanel table, int row, string labelText, Control control)
    {
        table.Controls.Add(new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        }, 0, row);
        table.Controls.Add(control, 1, row);
    }
}
