using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MajesticAssistant.Services;

/// <summary>
/// Registers a single system-wide hotkey via the Win32 RegisterHotKey API and raises
/// <see cref="Pressed"/> whenever it fires — this is what lets Alt+Space toggle the overlay
/// even while a fullscreen game has focus, which a WPF KeyDown handler alone cannot do.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    [Flags]
    public enum Modifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
    }

    /// <summary>Just the virtual-key codes this app actually binds to — avoids pulling in
    /// System.Windows.Forms (and its WinExe/UseWindowsForms baggage) for a single enum value.</summary>
    public enum Key : uint
    {
        Space = 0x20,
    }

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x4D41; // arbitrary id, unique within this process

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private bool _registered;

    public event Action? Pressed;

    /// <summary>
    /// Registers the hotkey against the given window. Call once the window's handle exists
    /// (i.e. after <see cref="Window.SourceInitialized"/>), since RegisterHotKey needs an HWND.
    /// </summary>
    public void Register(Window window, Modifiers modifiers, Key key)
    {
        var helper = new WindowInteropHelper(window);
        helper.EnsureHandle(); // force HWND creation now — the window stays hidden/unshown otherwise
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);

        _registered = RegisterHotKey(helper.Handle, HotkeyId, (uint)modifiers, (uint)key);
        if (!_registered)
        {
            // Most common cause: another application already owns this exact combo.
            // Non-fatal — the app still works, just without the global toggle.
            System.Diagnostics.Debug.WriteLine("HotkeyService: RegisterHotKey failed — combo may already be in use.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}
