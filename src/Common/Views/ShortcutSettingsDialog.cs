using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace ZwcadBatchPlot;

/// <summary>
/// 快捷键设置对话框 — WinForms 壳 + ElementHost 承载 WPF 控件。
/// </summary>
public sealed class ShortcutSettingsDialog : Form
{
    private readonly ShortcutSettingsControl? _wpfControl;

    /// <summary>用户确认后的别名表（原始命令名 → 简化命令），仅含非空项。</summary>
    public IReadOnlyDictionary<string, string> Aliases =>
        _wpfControl?.Aliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static bool _wpfInitialized;

    private static void EnsureWpfInitialized()
    {
        if (_wpfInitialized) return;
        _wpfInitialized = true;
        if (System.Windows.Application.Current == null)
            new System.Windows.Application();
    }

    public ShortcutSettingsDialog(IReadOnlyDictionary<string, string> currentAliases)
    {
        try
        {
            EnsureWpfInitialized();
            Text = "快捷键设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            UiLayout.ConfigureForm(this, 520, 380, 460, 320);
            ClientSize = new Size(UiLayout.Scale(520), UiLayout.Scale(380));

            _wpfControl = new ShortcutSettingsControl(currentAliases);
            _wpfControl.OkRequested += () => { DialogResult = DialogResult.OK; Close(); };
            _wpfControl.CancelRequested += () => { DialogResult = DialogResult.Cancel; Close(); };

            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = _wpfControl });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"快捷键设置对话框初始化失败:\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
