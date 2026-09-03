using System;

namespace ZwcadBatchPlot;

/// <summary>
/// 图号重排对话框 — WPF 窗口承载 DrawingNumberReorderControl。
/// </summary>
public sealed partial class DrawingNumberReorderDialog : System.Windows.Window
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

    public DrawingNumberReorderDialog(int jobCount, string detectedPrefix = "", bool horizontalFirst = false)
    {
        try
        {
            InitializeComponent();
            // 控件构造需要参数，因此在代码中创建并填充窗口内容。
            _wpfControl = new DrawingNumberReorderControl(jobCount, detectedPrefix, horizontalFirst);
            _wpfControl.OkRequested += () => { DialogResult = true; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = false; Close(); };
            _wpfControl.PreviewRequested += () => PreviewRequested?.Invoke();
            Content = _wpfControl;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"图号重排对话框初始化失败:\n{ex}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
