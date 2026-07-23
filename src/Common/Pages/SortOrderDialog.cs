using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace ZwcadBatchPlot;

/// <summary>
/// 排序方式选择对话框 — WinForms 壳 + ElementHost 承载 WPF 控件。
/// </summary>
public sealed class SortOrderDialog : Form
{
    private readonly SortOrderControl? _wpfControl;

    /// <summary>true = 从左到右，从上到下；false = 从上到下，从左到右。</summary>
    public bool HorizontalFirst => _wpfControl?.HorizontalFirst ?? false;

    private static bool _wpfInitialized;

    private static void EnsureWpfInitialized()
    {
        if (_wpfInitialized) return;
        _wpfInitialized = true;
        if (System.Windows.Application.Current == null)
            new System.Windows.Application();
    }

    public SortOrderDialog(bool horizontalFirst = false)
    {
        try
        {
            EnsureWpfInitialized();
            Text = "排序设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            UiLayout.ConfigureForm(this, 440, 220, 400, 200);
            ClientSize = new Size(UiLayout.Scale(440), UiLayout.Scale(220));

            _wpfControl = new SortOrderControl(horizontalFirst);
            _wpfControl.OkRequested += () => { DialogResult = DialogResult.OK; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = _wpfControl });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"排序设置对话框初始化失败:\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
