using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MajesticAssistant.Services;

/// <summary>
/// <c>ShowInTaskbar="False"</c> alone doesn't reliably keep a WPF window out of the Alt+Tab
/// switcher — that needs the WS_EX_TOOLWINDOW extended style, which WPF has no direct property
/// for. This applies it via raw Win32 so the overlay never shows up as a switchable app.
/// </summary>
public static class WindowStyleHelper
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public static void HideFromAltTab(Window window)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle();
        var exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
        exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
    }
}
