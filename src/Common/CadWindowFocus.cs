using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZwcadBatchPlot;

/// <summary>
/// CAD 内嵌 WinForms 隐藏后，Windows 可能把前台焦点交给其他进程。
/// 所有需要回到图面取点的流程统一通过本类把焦点交还当前 CAD 主窗口。
/// </summary>
internal static class CadWindowFocus
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    public static void ActivateCadWindow()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var windowHandle = process.MainWindowHandle;
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(windowHandle))
            {
                ShowWindowAsync(windowHandle, SwRestore);
            }

            BringWindowToTop(windowHandle);
            SetForegroundWindow(windowHandle);
        }
        catch
        {
            // Core Console 没有主窗口；焦点恢复失败也不能阻断图框编辑和框选。
        }
    }

    /// <summary>隐藏插件窗口并立即把输入焦点交给 CAD，避免焦点落到其他程序。</summary>
    public static void HideForCadInput(Form form)
    {
        form.Hide();
        ActivateCadWindow();
        System.Windows.Forms.Application.DoEvents();
        // DoEvents 可能继续处理一次失焦消息，因此处理完后再次确认 CAD 位于前台。
        ActivateCadWindow();
    }

    /// <summary>CAD 取点结束后恢复原窗口并置顶，保持原有模态窗口链。</summary>
    public static void RestoreDialog(Form form)
    {
        ActivateCadWindow();
        form.Visible = true;
        form.BringToFront();
        form.Activate();
    }
}
