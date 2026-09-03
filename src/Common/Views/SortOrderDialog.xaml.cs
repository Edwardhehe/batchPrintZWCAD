using System;

namespace ZwcadBatchPlot;

/// <summary>
/// 排序方式选择对话框 — WPF 窗口承载 SortOrderControl。
/// </summary>
public sealed partial class SortOrderDialog : System.Windows.Window
{
    private readonly SortOrderControl? _wpfControl;

    /// <summary>true = 从左到右，从上到下；false = 从上到下，从左到右。</summary>
    public bool HorizontalFirst => _wpfControl?.HorizontalFirst ?? false;
    /// <summary>图框块批量打印的主排序依据。</summary>
    public TitleBlockSortMode SortMode => _wpfControl?.SortMode ?? TitleBlockSortMode.DrawingNumber;

    public SortOrderDialog(
        bool horizontalFirst = false,
        bool showSortBasis = false,
        TitleBlockSortMode sortMode = TitleBlockSortMode.DrawingNumber)
    {
        try
        {
            InitializeComponent();
            // 控件构造需要参数，因此在代码中创建并填充窗口内容。
            _wpfControl = new SortOrderControl(horizontalFirst, showSortBasis, sortMode);
            _wpfControl.OkRequested += () => { DialogResult = true; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = false; Close(); };
            Content = _wpfControl;

            // 与原 WinForms 壳一致：显示排序依据时对话框更高。
            if (showSortBasis)
            {
                Height = 300;
                MinHeight = 200;
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"排序设置对话框初始化失败:\n{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
