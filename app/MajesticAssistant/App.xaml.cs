using System;
using System.Windows;

namespace MajesticAssistant;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A WinExe with no window yet has no console and no visible UI either — without this,
        // any exception during startup (hotkey registration, tray icon creation, etc.) just kills
        // the process with nothing to look at. Surface it in a message box instead.
        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatalError(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowFatalError(ex);
        };

        try
        {
            // Created but not shown: the overlay only appears once the user presses the
            // global hotkey (see MainWindow's HotkeyService wiring), matching how a
            // game overlay is expected to behave — invisible until summoned.
            var window = new MainWindow();
            MainWindow = window;
            window.Hide();
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
        }
    }

    private static void ShowFatalError(Exception ex)
    {
        MessageBox.Show(ex.ToString(), "Majestic Assistant — ошибка запуска",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
