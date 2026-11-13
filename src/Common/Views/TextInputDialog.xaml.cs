using System;
using System.Windows;
using System.Windows.Controls;

namespace ZwcadBatchPlot;

/// <summary>
/// 通用单行文本输入窗口（签章库重命名、三点图签重命名共用）。
/// 用 ShowDialog / CadDialog.ShowModal 打开；确定返回去首尾空格的输入，取消返回 null。
/// </summary>
public partial class TextInputDialog : Window
{
    private readonly Func<string, string?>? _validate;
    private readonly string _emptyHint;

    private TextInputDialog(string title, string prompt, string initialValue, string emptyHint, Func<string, string?>? validate)
    {
        InitializeComponent();

        Title = title;
        _prompt.Text = prompt;
        _emptyHint = emptyHint ?? "";
        _validate = validate;
        _input.Text = initialValue ?? "";
        _hint.Text = _emptyHint;

        Loaded += (_, __) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private string Value => _input.Text.Trim();

    public static string? Prompt(Window owner, string title, string prompt, string? initial, Func<string, string?>? validate = null)
    {
        var dialog = new TextInputDialog(title, prompt, initial ?? "", "", validate);
        return CadDialog.ShowModal(dialog, owner) == true ? dialog.Value : null;
    }

    /// <summary>弹出输入窗口；取消或输入为空时返回 null。</summary>
    public static string? Ask(Window? owner, string title, string prompt, string initialValue, string emptyHint = "")
    {
        var dialog = new TextInputDialog(title, prompt, initialValue, emptyHint, validate: null);
        if (owner != null)
        {
            return CadDialog.ShowModal(dialog, owner) == true ? dialog.Value : null;
        }

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        _hint.Text = Value.Length == 0 ? _emptyHint : "";
        _errorLabel.Text = "";
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            var error = "名称不能为空。";
            if (!string.IsNullOrEmpty(_emptyHint))
            {
                _hint.Text = _emptyHint;
            }
            _errorLabel.Text = error;
            MessageBox.Show(error, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            _input.Focus();
            return;
        }

        var validateError = _validate?.Invoke(Value);
        if (!string.IsNullOrWhiteSpace(validateError))
        {
            _errorLabel.Text = validateError;
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
