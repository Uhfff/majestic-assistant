using System.Windows;

namespace MajesticAssistant;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Created but not shown: the overlay only appears once the user presses the
        // global hotkey (see MainWindow's HotkeyService wiring), matching how a
        // game overlay is expected to behave — invisible until summoned.
        var window = new MainWindow();
        MainWindow = window;
        window.Hide();
    }
}
