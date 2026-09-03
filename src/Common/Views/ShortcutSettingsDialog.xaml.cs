using System;
using System.Collections.Generic;

namespace ZwcadBatchPlot;

/// <summary>
/// 快捷键设置对话框 — WPF 窗口承载 ShortcutSettingsControl。
/// </summary>
public sealed partial class ShortcutSettingsDialog : System.Windows.Window
{
    private readonly ShortcutSettingsControl? _wpfControl;

    /// <summary>用户确认后的别名表（原始命令名 → 简化命令），仅含非空项。</summary>
    public IReadOnlyDictionary<string, string> Aliases =>
        _wpfControl?.Aliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ShortcutSettingsDialog(IReadOnlyDictionary<string, string> currentAliases)
    {
        try
        {
            InitializeComponent();
            // 控件构造需要参数，因此在代码中创建并填充窗口内容。
            _wpfControl = new ShortcutSettingsControl(currentAliases);
            _wpfControl.OkRequested += () => { DialogResult = true; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = false; Close(); };
            Content = _wpfControl;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"快捷键设置初始化失败:\n{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
