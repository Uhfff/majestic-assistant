using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MajesticAssistant.Services;

/// <summary>
/// The app's only taskbar presence — a Windows Forms <see cref="NotifyIcon"/> hosted inside the
/// WPF app (NotifyIcon just needs a Win32 message pump on its creating thread, which WPF's own
/// Dispatcher already provides, so no separate Application.Run() is needed). Replaces the earlier
/// "right-click the header to exit" placeholder from Этап 1-3 with a real show/hide + quit menu,
/// since a window with ShowInTaskbar="False" would otherwise have no way to be closed at all.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly NotifyIcon _notifyIcon;

    public TrayIconService(Action onToggle, Action onExit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать / скрыть", null, (_, _) => onToggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => onExit());

        _notifyIcon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "Majestic Assistant — Alt+Space",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                onToggle();
        };
    }

    /// <summary>Draws the tray glyph at runtime (navy circle, white "M", red brand dot) instead of
    /// shipping a separate .ico asset — keeps the tray icon in sync with the theme's brand colors
    /// defined in Theme/DarkGlassTheme.xaml with nothing to regenerate by hand.</summary>
    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var bg = new SolidBrush(ColorTranslator.FromHtml("#1A2550"));
            g.FillEllipse(bg, 0, 0, 32, 32);

            using var font = new Font("Segoe UI", 16, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Color.White);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("M", font, textBrush, new RectangleF(0, -1, 32, 32), format);

            using var redBrush = new SolidBrush(ColorTranslator.FromHtml("#FF3B4E"));
            g.FillEllipse(redBrush, 21, 19, 8, 8);
        }

        // GetHicon() hands back an unmanaged icon handle that Icon.Dispose() does NOT free — clone
        // into a fully managed Icon, then explicitly destroy the original handle to avoid a GDI leak.
        var hIcon = bmp.GetHicon();
        try
        {
            using var handleIcon = Icon.FromHandle(hIcon);
            return (Icon)handleIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
