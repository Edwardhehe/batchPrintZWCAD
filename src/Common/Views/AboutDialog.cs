using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;

namespace ZwcadBatchPlot;

public sealed class AboutDialog : Form
{
    private static bool _wpfInitialized;

    private static void EnsureWpfInitialized()
    {
        if (_wpfInitialized) return;
        _wpfInitialized = true;
        if (System.Windows.Application.Current == null)
            new System.Windows.Application();
    }

    public AboutDialog()
    {
        try
        {
            EnsureWpfInitialized();
            Text = "关于 LA批量打印";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            UiLayout.ConfigureForm(this, 370, 260, 330, 240);
            ClientSize = new Size(UiLayout.Scale(370), UiLayout.Scale(260));

            var control = new AboutControl();
            control.OkRequested += () => { DialogResult = DialogResult.OK; Close(); };

            Controls.Add(new ElementHost { Dock = DockStyle.Fill, Child = control });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"关于对话框初始化失败:\n{ex.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
