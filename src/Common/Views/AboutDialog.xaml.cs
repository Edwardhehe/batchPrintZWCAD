using System;

namespace ZwcadBatchPlot;

/// <summary>
/// 关于对话框 — WPF 窗口承载 AboutControl。
/// </summary>
public sealed partial class AboutDialog : System.Windows.Window
{
    public AboutDialog()
    {
        try
        {
            InitializeComponent();
            Control.OkRequested += () => { DialogResult = true; Close(); };
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"关于对话框初始化失败:\n{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
