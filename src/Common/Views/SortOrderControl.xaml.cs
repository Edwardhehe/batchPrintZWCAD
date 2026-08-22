using System;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;

/// <summary>排序方式选择 WPF 控件。</summary>
public sealed partial class SortOrderControl : UserControl
{
    /// <summary>true = 从左到右，从上到下；false = 从上到下，从左到右。</summary>
    public bool HorizontalFirst => LeftToRightRadio.IsChecked == true;
    /// <summary>图框块批量打印的主排序依据；矩形框模式下固定返回 Spatial。</summary>
    public TitleBlockSortMode SortMode => SortBasisPanel.Visibility == Visibility.Visible
        ? (SpatialSortRadio.IsChecked == true ? TitleBlockSortMode.Spatial : TitleBlockSortMode.DrawingNumber)
        : TitleBlockSortMode.Spatial;

    public event Action? OkRequested;
    public event Action? CancelRequested;

    public SortOrderControl(
        bool horizontalFirst = false,
        bool showSortBasis = false,
        TitleBlockSortMode sortMode = TitleBlockSortMode.DrawingNumber)
    {
        InitializeComponent();
        SortBasisPanel.Visibility = showSortBasis ? Visibility.Visible : Visibility.Collapsed;
        DialogTitleText.Text = showSortBasis ? "选择批量打印排序方式" : "选择图纸位置排列顺序";
        if (sortMode == TitleBlockSortMode.Spatial)
            SpatialSortRadio.IsChecked = true;
        else
            DrawingNumberSortRadio.IsChecked = true;

        if (horizontalFirst)
            LeftToRightRadio.IsChecked = true;
        else
            TopToBottomRadio.IsChecked = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
