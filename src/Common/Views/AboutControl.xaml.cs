using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Clipboard = System.Windows.Clipboard;

namespace ZwcadBatchPlot;

public sealed partial class AboutControl : UserControl
{
    public event Action? OkRequested;

    public AboutControl()
    {
        InitializeComponent();
        DataContext = new AboutViewModel();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => OkRequested?.Invoke();

    private void OnQqGroupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("829218271");
            MessageBox.Show("QQ群号 829218271 已复制到剪贴板。", "LA批量打印",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
            MessageBox.Show("QQ群号：829218271", "LA批量打印",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private sealed class AboutViewModel
    {
        public string VersionText
        {
            get
            {
                var version = typeof(AboutControl).Assembly.GetName().Version;
                if (version == null)
                {
                    return "";
                }

                // 修订号为 0 时显示三段，例如 V1.15.6；补丁号显示四段，例如 V1.15.6.2。
                return version.Revision > 0 ? "V" + version.ToString(4) : "V" + version.ToString(3);
            }
        }
    }
}
