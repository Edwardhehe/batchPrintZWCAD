using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace ZwcadBatchPlot;

/// <summary>
/// 图号重排对话框 — WinForms 壳 + ElementHost 承载 WPF 用户控件。
/// </summary>
public sealed class DrawingNumberReorderDialog : Form
{
    private readonly DrawingNumberReorderControl? _wpfControl;

    public string Prefix => _wpfControl?.Prefix ?? "";
    public string Suffix => _wpfControl?.Suffix ?? "";
    public int StartNumber => _wpfControl?.StartNumber ?? 1;
    /// <summary>0 = 自动按总张数推断；>0 = 固定位数。</summary>
    public int Digits => _wpfControl?.Digits ?? 0;
    /// <summary>true = 从左到右、从上到下；false = 从上到下、从左到右。</summary>
    public bool HorizontalFirst => _wpfControl?.HorizontalFirst ?? false;

    /// <summary>用户点击"预览顺序"时触发。</summary>
    public event Action? PreviewRequested;

    // 整个进程只需初始化一次 WPF Application。
    private static bool _wpfInitialized;

    private static void EnsureWpfInitialized()
    {
        if (_wpfInitialized) return;
        _wpfInitialized = true;
        if (System.Windows.Application.Current == null)
        {
            new System.Windows.Application();
        }
    }

    public DrawingNumberReorderDialog(int jobCount, string detectedPrefix = "", bool horizontalFirst = false)
    {
        try
        {
            EnsureWpfInitialized();

            Text = "图号重排";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            UiLayout.ConfigureForm(this, 390, 318, 370, 298);
            ClientSize = new Size(UiLayout.Scale(390), UiLayout.Scale(318));

            _wpfControl = new DrawingNumberReorderControl(jobCount, detectedPrefix, horizontalFirst);
            _wpfControl.OkRequested += () =>
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            _wpfControl.CancelRequested += () =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            _wpfControl.PreviewRequested += () => PreviewRequested?.Invoke();

            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = _wpfControl
            };
            Controls.Add(host);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"图号重排对话框初始化失败:\n{ex}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
