using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>快捷键设置 WPF 控件：左列为功能名称与原始命令，右列为简化命令输入框。</summary>
public sealed partial class ShortcutSettingsControl : UserControl
{
    private readonly List<AliasRow> _rows = new();

    public event Action? OkRequested;
    public event Action? CancelRequested;

    /// <summary>校验通过后的别名表（原始命令名 → 简化命令），仅含非空项。</summary>
    public IReadOnlyDictionary<string, string> Aliases { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ShortcutSettingsControl(IReadOnlyDictionary<string, string> currentAliases)
    {
        InitializeComponent();
        BuildRows(currentAliases);
    }

    private void BuildRows(IReadOnlyDictionary<string, string> currentAliases)
    {
        foreach (var command in CommandAliasManager.AliasableCommands)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock
            {
                Text = command.DisplayName,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))
            });
            label.Children.Add(new TextBlock
            {
                Text = command.CommandName,
                Foreground = Brushes.Gray,
                FontSize = 10
            });

            var input = new TextBox
            {
                MaxLength = 16,
                MinHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = FindCurrentAlias(currentAliases, command.CommandName)
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(input, 1);
            grid.Children.Add(label);
            grid.Children.Add(input);
            RowsPanel.Children.Add(grid);
            _rows.Add(new AliasRow(command.CommandName, command.DisplayName, input));
        }
    }

    private static string FindCurrentAlias(IReadOnlyDictionary<string, string> currentAliases, string commandName)
    {
        foreach (var pair in currentAliases)
        {
            if (string.Equals(pair.Key, commandName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value ?? "";
            }
        }

        return "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var row in _rows)
        {
            var alias = row.Input.Text.Trim();
            if (alias.Length == 0)
            {
                continue;
            }

            if (!CommandAliasManager.IsValidAlias(alias))
            {
                errors.Add($"「{row.DisplayName}」的简化命令“{alias}”无效：需以字母开头，只含字母和数字，最长 16 位。");
                continue;
            }

            if (!usedAliases.Add(alias))
            {
                errors.Add($"简化命令“{alias}”被多个命令重复使用。");
                continue;
            }

            aliases[row.CommandName] = alias;
        }

        if (errors.Count > 0)
        {
            System.Windows.MessageBox.Show(
                string.Join("\n", errors),
                "快捷键设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Aliases = aliases;
        OkRequested?.Invoke();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    private sealed class AliasRow
    {
        internal AliasRow(string commandName, string displayName, TextBox input)
        {
            CommandName = commandName;
            DisplayName = displayName;
            Input = input;
        }

        internal string CommandName { get; }
        internal string DisplayName { get; }
        internal TextBox Input { get; }
    }
}
