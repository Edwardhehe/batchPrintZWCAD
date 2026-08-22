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
    /// <summary>图框块批量打印的主排序依据。</summary>
    public TitleBlockSortMode SortMode => _wpfControl?.SortMode ?? TitleBlockSortMode.DrawingNumber;

    private static bool _wpfInitialized;

    private static void EnsureWpfInitialized()
    {
        if (_wpfInitialized) return;
        _wpfInitialized = true;
        if (System.Windows.Application.Current == null)
            new System.Windows.Application();
    }

    public SortOrderDialog(
        bool horizontalFirst = false,
        bool showSortBasis = false,
        TitleBlockSortMode sortMode = TitleBlockSortMode.DrawingNumber)
    {
        try
        {
            EnsureWpfInitialized();
            Text = "排序设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            var dialogHeight = showSortBasis ? 300 : 220;
            UiLayout.ConfigureForm(this, 440, dialogHeight, 400, 200);
            ClientSize = new Size(UiLayout.Scale(440), UiLayout.Scale(dialogHeight));

            _wpfControl = new SortOrderControl(horizontalFirst, showSortBasis, sortMode);
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
