using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace ZwcadBatchPlot;

/// <summary>
/// 图号重排对话框 — 输入前缀/后缀、起始图号、排序方向，预览重排效果。
/// WinForms 壳 + ElementHost 承载 WPF 用户控件，API 保持不变。
/// </summary>
public sealed class DrawingNumberReorderDialog : Form
{
    private readonly DrawingNumberReorderControl _wpfControl;

    public string Prefix => _wpfControl.Prefix;
    public string Suffix => _wpfControl.Suffix;
    public int StartNumber => _wpfControl.StartNumber;

    /// <summary>排序方向：先水平再垂直（从左到右，从上到下）</summary>
    public bool HorizontalFirst => _wpfControl.HorizontalFirst;

    /// <summary>用户点击"预览顺序"时触发，供父窗口临时更新编号和叠加层。</summary>
    public event Action? PreviewRequested;

    public DrawingNumberReorderDialog(int jobCount, string detectedPrefix = "")
    {
        Text = "图号重排";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        UiLayout.ConfigureForm(this, 390, 290, 370, 270);
        ClientSize = new Size(UiLayout.Scale(390), UiLayout.Scale(270));

        _wpfControl = new DrawingNumberReorderControl(jobCount, detectedPrefix);
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

}
