using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using MajesticAssistant.Models;
using MajesticAssistant.Services;
using MajesticAssistant.Services.Rag;
using MajesticAssistant.Services.Voice;

namespace MajesticAssistant;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkey = new();
    private readonly RagService _rag;
    private readonly TrayIconService _tray;
    private readonly VoiceInputService _voice;
    private readonly string _settingsPath;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _askCts;
    private bool _kbReady;
    private bool _isRecording;

    public MainWindow()
    {
        InitializeComponent();

        // The app starts with this window hidden (see App.xaml.cs), and Hide() on a window
        // that has never been shown does NOT create its HWND — so without this explicit call,
        // SourceInitialized would never fire and the hotkey would never get registered.
        // EnsureHandle() forces window creation now, synchronously, without showing anything.
        new WindowInteropHelper(this).EnsureHandle();

        // Alt+Space toggles the overlay — matches the hotkey documented in the UI footer
        // and in every mockup.
        _hotkey.Register(this, HotkeyService.Modifiers.Alt, HotkeyService.Key.Space);
        _hotkey.Pressed += () => Dispatcher.Invoke(Toggle);

        WindowStyleHelper.HideFromAltTab(this);

        var baseDir = AppContext.BaseDirectory;
        _settingsPath = Path.Combine(baseDir, "settings.json");
        _settings = SettingsService.Load(_settingsPath);

        if (_settings.WindowLeft is double left && _settings.WindowTop is double top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            PositionTopRight();
        }

        _rag = new RagService(
            kbRoot: Path.Combine(baseDir, "kb"),
            cachePath: Path.Combine(baseDir, "cache", "embeddings.json"));

        // Model names default to RagService's own built-in properties; a hand-edited settings.json
        // can override them without touching code (see README).
        if (!string.IsNullOrWhiteSpace(_settings.EmbedModel)) _rag.EmbedModel = _settings.EmbedModel;
        if (!string.IsNullOrWhiteSpace(_settings.ChatModel)) _rag.ChatModel = _settings.ChatModel;

        // The only way to fully quit now that the window has no taskbar entry — replaces the
        // Этап 1-3 "right-click the header" placeholder.
        _tray = new TrayIconService(onToggle: Toggle, onExit: () => Application.Current.Shutdown());

        _voice = new VoiceInputService(
            modelPath: Path.Combine(baseDir, "whisper", "ggml-small.bin"),
            tempWavPath: Path.Combine(baseDir, "cache", "voice-input.wav"));

        // Application.Shutdown() closes every tracked window (raising Closed) regardless of
        // ShutdownMode, so this fires whether the user quits via the tray menu or Alt+F4 ever
        // reaches this window — one place to release the hotkey/tray and persist the window spot.
        Closed += (_, _) =>
        {
            _hotkey.Dispose();
            _tray.Dispose();
            _voice.Dispose();
            SaveSettings();
        };

        // Kick indexing off immediately at process start, in the background, so the knowledge
        // base is (hopefully) already warm by the time the player first presses Alt+Space —
        // a cold index build over the whole kb can take a couple of minutes.
        _ = InitializeKnowledgeBaseAsync();
    }

    /// <summary>Pins the panel to the corner used throughout the design mockups
    /// (28px margin from the top and right edges of the primary monitor's work area).</summary>
    private void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 28;
        Top = workArea.Top + 28;
    }

    /// <summary>Guards against restoring a position from a monitor that's no longer connected
    /// (laptop undocked, monitor count changed since the position was saved).</summary>
    private static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
        top >= SystemParameters.VirtualScreenTop - 50 &&
        top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private void SaveSettings()
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        SettingsService.Save(_settingsPath, _settings);
    }

    private async Task InitializeKnowledgeBaseAsync()
    {
        SetAnswerText("Загружаю базу знаний…");
        QueryInput.IsEnabled = false;

        try
        {
            await _rag.InitializeAsync(status => Dispatcher.BeginInvoke(() => SetAnswerText(status)));
            _kbReady = true;
            SetAnswerText("Готов отвечать на вопросы по правилам сервера.");
        }
        catch (Exception ex)
        {
            // Most likely cause: Ollama isn't installed/running, or the models aren't pulled yet —
            // RagService.InitializeAsync already puts a specific, actionable message in ex.Message.
            SetAnswerText(ex.Message);
        }
        finally
        {
            QueryInput.IsEnabled = true;
        }
    }

    public void Toggle()
    {
        if (IsVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    private void ShowOverlay()
    {
        Show();
        Activate();
        QueryInput.Focus();
        Keyboard.Focus(QueryInput);
        ((Storyboard)FindResource("EnterAnimation")).Begin(this);
    }

    private void HideOverlay()
    {
        Hide();
    }

    // Clicking into the game (or any other window) deactivates us — hide rather than sit on
    // top of gameplay unfocused. This does NOT fire from clicking controls inside this window.
    private void Window_Deactivated(object sender, EventArgs e) => HideOverlay();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            HideOverlay();
    }

    private void QueryInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            SubmitQuery();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SubmitQuery();

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRecording)
        {
            try
            {
                _voice.StartRecording();
            }
            catch (Exception ex)
            {
                SetAnswerText(ex.Message);
                return;
            }

            _isRecording = true;
            MicButton.Content = "■";
            MicButton.ToolTip = "Остановить запись";
            return;
        }

        _isRecording = false;
        MicButton.Content = "●";
        MicButton.ToolTip = "Голосовой ввод";
        MicButton.IsEnabled = false;
        SetAnswerText("Распознаю голос…");

        try
        {
            var text = await _voice.StopRecordingAndTranscribeAsync();
            QueryInput.Text = text;
            QueryInput.CaretIndex = text.Length;
            SetAnswerText(_kbReady ? "Готов отвечать на вопросы по правилам сервера." : "");
        }
        catch (Exception ex)
        {
            SetAnswerText($"Не получилось распознать речь: {ex.Message}");
        }
        finally
        {
            MicButton.IsEnabled = true;
        }
    }

    private void SubmitQuery()
    {
        var question = QueryInput.Text.Trim();
        if (question.Length == 0)
            return;

        if (!_kbReady)
        {
            SetAnswerText("База знаний ещё загружается, подожди немного и попробуй снова.");
            return;
        }

        // A new question cancels whatever answer was still streaming in — the newest question
        // the player asked is the one they want answered, not a queue of stale ones.
        _askCts?.Cancel();
        _askCts = new CancellationTokenSource();
        var ct = _askCts.Token;

        QueryInput.Clear();
        SetAnswerText("");

        _ = RunAskAsync(question, ct);
    }

    private async Task RunAskAsync(string question, CancellationToken ct)
    {
        // A one-element array standing in for a mutable "still on the first token?" flag that the
        // onToken closure below can flip — a plain bool local would need to be captured by
        // reference across the async callback, which C# doesn't allow for method parameters.
        var isFirst = new[] { true };
        try
        {
            await _rag.AskAsync(question, token =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (isFirst[0])
                    {
                        AnswerText.Text = token;
                        isFirst[0] = false;
                    }
                    else
                    {
                        AnswerText.Text += token;
                    }
                });
            }, topK: 6, ct: ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer question — nothing to show, the new answer is already streaming.
        }
        catch (Exception ex)
        {
            // Discarded: DispatcherOperation is awaitable (hence CS4014 without this), but there's
            // nothing to await for — same fire-and-forget UI update as everywhere else in this file.
            _ = Dispatcher.BeginInvoke(() => SetAnswerText($"Не получилось получить ответ: {ex.Message}"));
        }
    }

    private void SetAnswerText(string text) => AnswerText.Text = text;

    private void CloseGlyph_Click(object sender, RoutedEventArgs e) => HideOverlay();

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Lets the user drag the panel to a spot that doesn't cover anything important in
        // their game of choice — the corner position is a default, not a hard requirement.
        try { DragMove(); } catch (InvalidOperationException) { /* button released mid-call */ }
    }
}
