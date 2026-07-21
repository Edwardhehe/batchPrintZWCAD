using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>
/// 图号重排 WPF 用户控件 — 功能与旧 WinForms 版本完全一致。
/// </summary>
public sealed partial class DrawingNumberReorderControl : UserControl
{
    private readonly int _jobCount;

    public string Prefix => PrefixBox.Text.Trim();
    public string Suffix => SuffixBox.Text.Trim();
    public int StartNumber => int.TryParse(StartNumberBox.Text, out var v) ? Math.Max(1, Math.Min(9999, v)) : 1;

    /// <summary>排序方向：先水平再垂直（从左到右，从上到下）</summary>
    public bool HorizontalFirst => SortOrderCombo.SelectedIndex == 1;

    /// <summary>用户点击"预览顺序"时触发。</summary>
    public event Action? PreviewRequested;

    /// <summary>用户点击"确定"时触发。</summary>
    public event Action? OkRequested;

    /// <summary>用户点击"取消"或关闭窗口时触发。</summary>
    public event Action? CancelRequested;

    public DrawingNumberReorderControl(int jobCount, string detectedPrefix = "")
    {
        InitializeComponent();
        _jobCount = jobCount;
        PrefixBox.Text = detectedPrefix;
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var prefix = Prefix;
        var suffix = Suffix;
        var start = StartNumber;
        // 同步 TextBox 避免输入非数字后属性返回的不是文本值
        StartNumberBox.Text = start.ToString(CultureInfo.InvariantCulture);
        var digits = Math.Max(2, (_jobCount + start - 1).ToString().Length);
        var samples = Math.Min(5, _jobCount);
        var items = new string[samples];
        for (var i = 0; i < samples; i++)
        {
            items[i] = prefix + (start + i).ToString($"D{digits}") + suffix;
        }

        var tail = _jobCount > samples ? " ..." : "";
        PreviewLabel.Content = $"示例: {string.Join(", ", items)}{tail}  (共{_jobCount}张)";
    }

    private void OnTextChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnStartNumberPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // 只允许输入数字
        e.Handled = e.Text.Any(c => !char.IsDigit(c));
    }

    private void OnStartNumberUp(object sender, RoutedEventArgs e)
    {
        var v = StartNumber;
        if (v < 9999) { StartNumberBox.Text = (v + 1).ToString(CultureInfo.InvariantCulture); }
    }

    private void OnStartNumberDown(object sender, RoutedEventArgs e)
    {
        var v = StartNumber;
        if (v > 1) { StartNumberBox.Text = (v - 1).ToString(CultureInfo.InvariantCulture); }
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();

    private void OnPreviewClick(object sender, RoutedEventArgs e) => PreviewRequested?.Invoke();

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
}
