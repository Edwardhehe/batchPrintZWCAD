using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace ZwcadBatchPlot;

/// <summary>
/// 统一的 WPF 窗口显示入口，替代原 CadApp.ShowModalDialog(Form)/ShowModelessDialog(Form)。
/// 通过 WindowInteropHelper 把属主设为 CAD 主窗口句柄：模态时 ShowDialog 自动禁用属主，
/// 非模态时保证窗口随宿主显示。AutoCAD 与中望平台共用同一实现。
/// </summary>
internal static class CadDialog
{
    public static bool? ShowModal(Window window)
    {
        SetOwner(window);
        return window.ShowDialog();
    }

    public static void ShowModeless(Window window)
    {
        SetOwner(window);
        window.Show();
    }

    private static void SetOwner(Window window)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var ownerHandle = process.MainWindowHandle;
            if (ownerHandle != IntPtr.Zero)
            {
                new WindowInteropHelper(window) { Owner = ownerHandle };
            }
        }
        catch
        {
            // 取不到宿主窗口句柄时按无属主窗口显示。
        }
    }
}
