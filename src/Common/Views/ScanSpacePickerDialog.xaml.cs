using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>
/// 多文件批打前勾选模型/布局。按文件分组的扁平列表（非折叠树）。
/// 调用方用 <see cref="CadDialog.ShowModal"/> 显示；DialogResult=true 表示开始扫描。
/// </summary>
public sealed partial class ScanSpacePickerDialog : Window
{
    private sealed class FileGroup
    {
        public string FilePath { get; set; } = "";
        public CheckBox HeaderCheck { get; set; } = null!;
        public ComboBox StyleCombo { get; set; } = null!;
        public List<SpaceRow> Rows { get; } = new();
        public bool SuppressHeaderSync;
        public bool SuppressChildSync;
    }

    private sealed class SpaceRow
    {
        public DwgSpaceEntry Entry { get; set; } = null!;
        public CheckBox Check { get; set; } = null!;
        public FileGroup Group { get; set; } = null!;
    }

    private readonly List<FileGroup> _groups = new();
    private readonly List<DwgSpaceEntry> _selected = new();

    /// <summary>用户勾选并确认开始扫描的空间。</summary>
    public IReadOnlyList<DwgSpaceEntry> SelectedSpaces => _selected;

    private readonly string _defaultStyleSheet;
    private readonly List<string> _availableStyles;

    public ScanSpacePickerDialog(
        IEnumerable<DwgSpaceEntry> spaces,
        string defaultStyleSheet,
        IEnumerable<string>? availableStyles = null)
    {
        InitializeComponent();
        _defaultStyleSheet = defaultStyleSheet ?? "";
        _availableStyles = (availableStyles ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_availableStyles.Count == 0)
        {
            try
            {
                _availableStyles = PlotStyleManager.GetAvailableCtbStyles().ToList();
            }
            catch
            {
                _availableStyles = new List<string>();
            }
        }

        BuildContent(spaces?.ToList() ?? new List<DwgSpaceEntry>());
    }

    private void BuildContent(List<DwgSpaceEntry> spaces)
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var top = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(top, Dock.Top);

        var buttons = new WrapPanel
        {
            Orientation = Orientation.Horizontal
        };
        buttons.Children.Add(MakeQuickSelectButton("全选", "勾选全部模型与布局", () => SetAllSelected(true)));
        buttons.Children.Add(MakeQuickSelectButton("全不选", "取消全部勾选", () => SetAllSelected(false)));
        buttons.Children.Add(MakeQuickSelectButton(
            "当前布局/模型",
            "每个文件只勾选保存时停留的那个模型或布局（列表中带「当前」标记）",
            SelectLastActiveSpaces));
        buttons.Children.Add(MakeQuickSelectButton("仅模型", "每个文件只勾选模型空间", SelectModelSpacesOnly));
        buttons.Children.Add(MakeQuickSelectButton("仅布局", "每个文件只勾选布局（不含模型）", SelectPaperSpacesOnly));
        top.Children.Add(buttons);
        top.Children.Add(new TextBlock
        {
            Text = "勾选需要扫描的模型/布局，确认后将清空清单并重新扫描。带「（当前）」的是该文件保存时停留的空间。",
            Foreground = Brushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(top);

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0)
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 76,
            Style = TryFindResource("PluginButtonStyle") as Style,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, __) =>
        {
            DialogResult = false;
            Close();
        };
        var start = new Button
        {
            Content = "开始扫描",
            MinWidth = 104,
            Style = TryFindResource("PluginButtonStyle") as Style,
            IsDefault = true
        };
        start.Click += (_, __) => ConfirmStart();
        bottom.Children.Add(cancel);
        bottom.Children.Add(start);
        root.Children.Add(bottom);

        var listHost = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xCD, 0xD2, 0xD8)),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            Padding = new Thickness(4)
        };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var stack = new StackPanel();
        scroll.Content = stack;
        listHost.Child = scroll;
        root.Children.Add(listHost);

        foreach (var fileGroup in spaces.GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            var group = new FileGroup { FilePath = fileGroup.Key };
            var headerRow = new DockPanel
            {
                Margin = new Thickness(2, 4, 2, 2),
                LastChildFill = true
            };

            var styleCombo = new ComboBox
            {
                Width = 168,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "打印样式"
            };
            foreach (var style in _availableStyles)
            {
                styleCombo.Items.Add(style);
            }

            PlotStyleManager.RestoreSavedStyle(styleCombo, _defaultStyleSheet);
            group.StyleCombo = styleCombo;

            var styleLabel = new TextBlock
            {
                Text = "打印样式",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = Brushes.DimGray
            };
            DockPanel.SetDock(styleCombo, Dock.Right);
            DockPanel.SetDock(styleLabel, Dock.Right);
            headerRow.Children.Add(styleCombo);
            headerRow.Children.Add(styleLabel);

            var header = new CheckBox
            {
                Content = Path.GetFileName(fileGroup.Key),
                IsThreeState = true,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold,
                ToolTip = fileGroup.Key
            };
            group.HeaderCheck = header;
            header.Checked += (_, __) => OnHeaderChanged(group, true);
            header.Unchecked += (_, __) => OnHeaderChanged(group, false);
            // 三态点选到 null（部分选）时，按「全选」处理，避免卡在中间态。
            header.Indeterminate += (_, __) =>
            {
                if (group.SuppressHeaderSync)
                {
                    return;
                }

                header.IsChecked = true;
            };
            headerRow.Children.Add(header);
            stack.Children.Add(headerRow);

            foreach (var entry in fileGroup)
            {
                var row = new SpaceRow { Entry = entry, Group = group };
                var child = new CheckBox
                {
                    Content = entry.DisplayName,
                    IsChecked = entry.Selected,
                    Margin = new Thickness(22, 1, 2, 1),
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                row.Check = child;
                child.Checked += (_, __) => OnChildChanged(row, true);
                child.Unchecked += (_, __) => OnChildChanged(row, false);
                group.Rows.Add(row);
                stack.Children.Add(child);
            }

            _groups.Add(group);
            SyncHeaderFromChildren(group);
        }

        Content = root;
    }

    private void OnHeaderChanged(FileGroup group, bool selected)
    {
        if (group.SuppressHeaderSync)
        {
            return;
        }

        group.SuppressChildSync = true;
        try
        {
            foreach (var row in group.Rows)
            {
                row.Entry.Selected = selected;
                row.Check.IsChecked = selected;
            }
        }
        finally
        {
            group.SuppressChildSync = false;
        }
    }

    private void OnChildChanged(SpaceRow row, bool selected)
    {
        if (row.Group.SuppressChildSync)
        {
            return;
        }

        row.Entry.Selected = selected;
        SyncHeaderFromChildren(row.Group);
    }

    private static void SyncHeaderFromChildren(FileGroup group)
    {
        group.SuppressHeaderSync = true;
        try
        {
            var total = group.Rows.Count;
            var checkedCount = group.Rows.Count(r => r.Check.IsChecked == true);
            if (checkedCount == 0)
            {
                group.HeaderCheck.IsChecked = false;
            }
            else if (checkedCount == total)
            {
                group.HeaderCheck.IsChecked = true;
            }
            else
            {
                group.HeaderCheck.IsChecked = null;
            }
        }
        finally
        {
            group.SuppressHeaderSync = false;
        }
    }

    /// <summary>创建顶部快速选择按钮。</summary>
    private Button MakeQuickSelectButton(string content, string toolTip, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            MinWidth = 76,
            Style = TryFindResource("PluginButtonStyle") as Style,
            Margin = new Thickness(0, 0, 6, 4),
            ToolTip = toolTip
        };
        button.Click += (_, __) => onClick();
        return button;
    }

    private void SetAllSelected(bool selected)
    {
        ApplySelection(_ => selected);
    }

    /// <summary>每个文件只勾选保存时的当前模型/布局（<see cref="DwgSpaceEntry.IsLastActive"/>）。</summary>
    private void SelectLastActiveSpaces()
    {
        ApplySelection(row => row.Entry.IsLastActive);
    }

    /// <summary>每个文件只勾选模型空间。</summary>
    private void SelectModelSpacesOnly()
    {
        ApplySelection(row => row.Entry.IsModelSpace);
    }

    /// <summary>每个文件只勾选布局（不含模型）。</summary>
    private void SelectPaperSpacesOnly()
    {
        ApplySelection(row => !row.Entry.IsModelSpace);
    }

    /// <summary>按谓词批量改勾选，并同步各文件头三态复选框。</summary>
    private void ApplySelection(Func<SpaceRow, bool> shouldSelect)
    {
        foreach (var group in _groups)
        {
            group.SuppressHeaderSync = true;
            group.SuppressChildSync = true;
            try
            {
                foreach (var row in group.Rows)
                {
                    var selected = shouldSelect(row);
                    row.Entry.Selected = selected;
                    row.Check.IsChecked = selected;
                }

                SyncHeaderFromChildren(group);
            }
            finally
            {
                group.SuppressHeaderSync = false;
                group.SuppressChildSync = false;
            }
        }
    }

    private void ConfirmStart()
    {
        _selected.Clear();
        foreach (var group in _groups)
        {
            var style = group.StyleCombo.SelectedItem?.ToString()
                ?? PlotStyleManager.NormalizeStyleName(_defaultStyleSheet);
            foreach (var row in group.Rows)
            {
                row.Entry.Selected = row.Check.IsChecked == true;
                row.Entry.StyleSheet = style;
                if (row.Entry.Selected)
                {
                    _selected.Add(row.Entry);
                }
            }
        }

        if (_selected.Count == 0)
        {
            MessageBox.Show("请至少勾选一个模型或布局。", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
        Close();
    }
}
