using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;

/// <summary>
/// 三点图签录入对话框：三个锚点点完之后弹出，集中定义名称、排序优先级与各字段范围。
/// 每个「范围」按钮先隐藏本窗再去图面框两个角点，取完自动恢复窗口；
/// × 清除该项。确定后由调用方读取 <see cref="Result"/> 保存。
/// </summary>
public partial class ThreePointCaptureForm : Window
{
    private const string FrameKey = "__frame__";

    private readonly ThreePointDefinition _draft;
    private readonly bool _allowRename;
    private readonly Dictionary<string, LocalRectangle> _fields = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _rangeButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _clearButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _requiredLabels = new(StringComparer.Ordinal);
    private LocalRectangle? _frameRegion;

    /// <summary>
    /// 图面预览回调：(字段键, 标签, 范围)。范围传 null 表示清除该键的红框。
    /// 由调用方用 <see cref="TransientFrameMarkers"/> 实现，本窗不直接引用 CAD 类型。
    /// </summary>
    private Action<string, string?, LocalRectangle?>? _previewRegion;
    private bool _previewAttached;

    /// <summary>确定后的定义（含名称、优先级、字段范围、图框范围）；取消时为 null。</summary>
    public ThreePointDefinition? Result { get; private set; }

    /// <summary>
    /// 用户点了「确定」。这里刻意不用 <see cref="Window.DialogResult"/>：
    /// 本窗由 CAD 的模态接口显示，在按钮 Click 里同步设置 DialogResult / 拆窗
    /// 会与 CAD 的模态循环相互重入（工程内已有先例：会抛 InvalidOperationException 直接崩 CAD）。
    /// 改为置标志 + 延后 Close，调用方只看这个标志。
    /// </summary>
    public bool Confirmed { get; private set; }

    public ThreePointCaptureForm(ThreePointDefinition draft, bool allowRename)
    {
        InitializeComponent();

        _draft = draft;
        _allowRename = allowRename;

        _nameBox.Text = draft.Name;
        _nameBox.IsReadOnly = !allowRename;
        _nameHint.Text = allowRename
            ? "名称自动取三个锚点文字，可在这里改。"
            : "重新录入时名称保持不变（要改名请回列表点「重命名」）。";

        for (var priority = 1; priority <= 9; priority++)
        {
            _priorityBox.Items.Add(priority);
        }

        var current = draft.Priority > 0 && draft.Priority <= 9 ? draft.Priority : 1;
        _priorityBox.SelectedItem = current;

        foreach (var pair in draft.Fields)
        {
            _fields[pair.Key] = pair.Value;
        }

        if (draft.SearchRegion != null
            && (draft.SearchRegion.MaxX - draft.SearchRegion.MinX) > 1e-6
            && (draft.SearchRegion.MaxY - draft.SearchRegion.MinY) > 1e-6)
        {
            _frameRegion = draft.SearchRegion;
        }

        BuildRows();
    }

    /// <summary>
    /// 接入图面红框预览。调用后立刻把已有范围（重新录入时库里存的字段）先画出来，
    /// 用户一打开窗口就能看到上次框的位置，不用挨个点「范围」确认。
    /// </summary>
    public void AttachRegionPreview(Action<string, string?, LocalRectangle?> previewRegion)
    {
        _previewRegion = previewRegion;
        _previewAttached = true;

        // 图面红框不加字段名文字：框里本来就有图签文字，叠一层标签会挡住判断框得准不准。
        foreach (var pair in _fields)
        {
            Preview(pair.Key, null, pair.Value);
        }

        if (_frameRegion != null)
        {
            Preview(FrameKey, null, _frameRegion);
        }
    }

    private void Preview(string key, string? label, LocalRectangle? region)
    {
        if (!_previewAttached || _previewRegion == null)
        {
            return;
        }

        try
        {
            _previewRegion(key, label, region);
        }
        catch (Exception ex)
        {
            // 预览失败（宿主不支持临时图素等）不能影响录入本身。
            BatchPlotCommands.LogCaptureStep($"preview region failed ({key}): {ex}");
        }
    }

    private void BuildRows()
    {
        _fieldPanel.Children.Clear();

        var anchorText = "锚点：" + (_draft.AnchorSummary == "未完成" ? "—" : _draft.AnchorSummary);
        if (_draft.IsCollinearAnchors)
        {
            anchorText += "\n三点共线：按相似变换识别（等比缩放 + 旋转 + 平移），不支持镜像与非等比缩放。";
        }

        var anchorHint = new TextBlock
        {
            Text = anchorText,
            Foreground = System.Windows.Media.Brushes.DimGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        _fieldPanel.Children.Add(anchorHint);

        foreach (var key in ThreePointFieldKeys.CaptureOrder)
        {
            var label = ThreePointFieldKeys.Display(key) + "范围";
            var required = ThreePointFieldKeys.IsRequired(key);
            AddRow(key, label, required, required ? "必填。" : "可选，不框也不会影响识别。");
        }

        AddRow(FrameKey, "图框范围", false, "该图签所在的图框，仅在试识别时用于核对；可不框。");
    }

    private void AddRow(string key, string label, bool required, string hint)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rangeButton = new Button
        {
            MinHeight = 24,
            Padding = new Thickness(4, 0, 4, 0),
            ToolTip = hint
        };
        ApplyButtonStyle(rangeButton);
        rangeButton.Click += (_, _) => PickRange(key, label);

        var clearButton = new Button
        {
            Content = "×",
            Width = 24,
            MinHeight = 24,
            Padding = new Thickness(0),
            IsEnabled = false,
            ToolTip = "清除该项范围"
        };
        ApplyButtonStyle(clearButton);
        clearButton.Click += (_, _) => ClearRange(key, label);

        Grid.SetColumn(rangeButton, 0);
        Grid.SetColumn(clearButton, 2);
        row.Children.Add(rangeButton);
        row.Children.Add(clearButton);
        _fieldPanel.Children.Add(row);

        _rangeButtons[key] = rangeButton;
        _clearButtons[key] = clearButton;
        _requiredLabels[key] = required;
        Refresh(key, label);
    }

    private void ApplyButtonStyle(Button button)
    {
        if (TryFindResource("PluginButtonStyle") is Style style)
        {
            button.Style = style;
        }
    }

    private void PickRange(string key, string label)
    {
        try
        {
            var rectangle = BatchPlotCommands.PickRectangle(this, label);
            if (rectangle == null)
            {
                _status.Text = $"「{label}」未取到范围（已取消或框得太小）。";
                return;
            }

            if (key == FrameKey)
            {
                _frameRegion = rectangle;
            }
            else
            {
                _fields[key] = rectangle;
            }

            Refresh(key, label);
            // 框完立刻在图面画红框（矩形 + 对角线），和「图框型」录入窗的核对方式一致。
            Preview(key, null, rectangle);
            _status.Text = $"已取「{label}」范围（{RegionSizeText(rectangle)}）。";
        }
        catch (Exception ex)
        {
            // 取点阶段任何异常都只报错，不能让它冒到 CAD 的模态循环里变成致命错误。
            BatchPlotCommands.LogCaptureStep($"pick range failed ({label}): {ex}");
            _status.Text = $"「{label}」取范围失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 范围的显示尺寸。UCS 与 WCS 不一致时框出的范围在 WCS 里是斜矩形，
    /// 包围盒尺寸会比实际框大一圈，这里优先用两个边长（ActualWidth/Height 已按相邻边填好）。
    /// </summary>
    private static string RegionSizeText(LocalRectangle rectangle)
    {
        var width = rectangle.ActualWidth > 1e-6
            ? rectangle.ActualWidth
            : rectangle.MaxX - rectangle.MinX;
        var height = rectangle.ActualHeight > 1e-6
            ? rectangle.ActualHeight
            : rectangle.MaxY - rectangle.MinY;
        return $"{width:0.##} × {height:0.##}";
    }

    private void ClearRange(string key, string label)
    {
        try
        {
            if (key == FrameKey)
            {
                _frameRegion = null;
            }
            else
            {
                _fields.Remove(key);
            }

            Refresh(key, label);
            Preview(key, null, null);
            _status.Text = $"已清除「{label}」范围。";
        }
        catch (Exception ex)
        {
            BatchPlotCommands.LogCaptureStep($"clear range failed ({label}): {ex}");
        }
    }

    private void Refresh(string key, string label)
    {
        var has = key == FrameKey ? _frameRegion != null : _fields.ContainsKey(key);
        var required = _requiredLabels.TryGetValue(key, out var value) && value;
        if (_rangeButtons.TryGetValue(key, out var rangeButton))
        {
            rangeButton.Content = (required ? "* " + label : label) + (has ? "  ✓" : "");
            rangeButton.FontWeight = has ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (_clearButtons.TryGetValue(key, out var clearButton))
        {
            clearButton.IsEnabled = has;
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = _nameBox.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("入库名称不能为空。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                _nameBox.Focus();
                return;
            }

            if (!_fields.ContainsKey(ThreePointFieldKeys.Title)
                || !_fields.ContainsKey(ThreePointFieldKeys.DrawingNumber))
            {
                MessageBox.Show("图名与图号的范围必须框选。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _draft.Name = name;
            _draft.Priority = _priorityBox.SelectedItem is int priority ? priority : 1;
            _draft.Fields = new Dictionary<string, LocalRectangle>(_fields, StringComparer.Ordinal);
            if (_frameRegion != null)
            {
                _draft.SearchRegion = _frameRegion;
            }

            Result = _draft;
            Confirmed = true;
            BatchPlotCommands.LogCaptureStep($"ok clicked: name={name}, fields={_fields.Count}");
        }
        catch (Exception ex)
        {
            BatchPlotCommands.LogCaptureStep("ok clicked failed: " + ex);
            MessageBox.Show("读取录入结果失败：\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        CloseDeferred();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        Confirmed = false;
        CloseDeferred();
    }

    /// <summary>Esc 关窗（不再用 IsCancel，避免 WPF 内部去设 DialogResult）。</summary>
    private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
        {
            return;
        }

        // 优先级下拉展开时，Esc 应该只收起下拉，不要顺手把整个窗口取消掉。
        if (_priorityBox.IsDropDownOpen)
        {
            return;
        }

        e.Handled = true;
        OnCancelClick(sender, e);
    }

    /// <summary>
    /// 把关窗排到消息队列下一轮：按钮 Click 的路由事件要完整退出后窗口才销毁，
    /// 否则 CAD 模态循环正在展开 Click 时拆窗口，容易连带把 CAD 一起弄崩。
    /// </summary>
    private void CloseDeferred()
    {
        Dispatcher.BeginInvoke(
            new Action(Close),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
