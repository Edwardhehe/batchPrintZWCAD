using System;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;

/// <summary>排序方式选择 WPF 控件。</summary>
public sealed partial class SortOrderControl : UserControl
{
    /// <summary>true = 从左到右，从上到下；false = 从上到下，从左到右。</summary>
    public bool HorizontalFirst => LeftToRightRadio.IsChecked == true;

    public event Action? OkRequested;
    public event Action? CancelRequested;

    public SortOrderControl(bool horizontalFirst = false)
    {
        InitializeComponent();
        if (horizontalFirst)
            LeftToRightRadio.IsChecked = true;
        else
            TopToBottomRadio.IsChecked = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
