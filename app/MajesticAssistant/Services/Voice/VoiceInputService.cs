using System.IO;
using System.Text;
using System.Threading.Tasks;
using NAudio.Wave;
using Whisper.net;

namespace MajesticAssistant.Services.Voice;

/// <summary>
/// Push-to-talk voice input: records the default microphone to a temp 16kHz mono WAV via NAudio,
/// then transcribes it locally with whisper.cpp (through the Whisper.net bindings) — no cloud
/// speech API involved, matching the rest of the app's local-only design. The GGML model file
/// isn't bundled (same convention as Ollama's models): the user downloads it once into whisper/
/// next to the exe — see the README.
/// </summary>
public sealed class VoiceInputService : IDisposable
{
    private const int SampleRateHz = 16000;

    private readonly string _modelPath;
    private readonly string _tempWavPath;

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private WhisperFactory? _factory;

    public VoiceInputService(string modelPath, string tempWavPath)
    {
        _modelPath = modelPath;
        _tempWavPath = tempWavPath;
    }

    public bool IsRecording => _waveIn is not null;

    public void StartRecording()
    {
        if (IsRecording)
            return;

        if (!File.Exists(_modelPath))
        {
            throw new InvalidOperationException(
                $"Не найдена модель распознавания речи: {_modelPath}. Скачай GGML-модель " +
                "whisper.cpp (например ggml-small.bin) и положи в папку whisper/ рядом с exe — " +
                "см. README.");
        }

        var dir = Path.GetDirectoryName(_tempWavPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRateHz, 16, 1) };
        var writer = new WaveFileWriter(_tempWavPath, waveIn.WaveFormat);
        waveIn.DataAvailable += (_, e) => writer.Write(e.Buffer, 0, e.BytesRecorded);

        _waveIn = waveIn;
        _writer = writer;
        waveIn.StartRecording();
    }

    /// <summary>Stops the microphone, then runs the recorded WAV through whisper.cpp. The whisper
    /// model is loaded on first use (not at app startup) since most sessions may never touch voice
    /// input, and loading a multi-hundred-MB GGML model isn't free.</summary>
    public async Task<string> StopRecordingAndTranscribeAsync()
    {
        if (_waveIn is null || _writer is null)
            throw new InvalidOperationException("Запись ещё не была начата.");

        _waveIn.StopRecording();
        _waveIn.Dispose();
        _waveIn = null;

        _writer.Dispose();
        _writer = null;

        _factory ??= WhisperFactory.FromPath(_modelPath);

        using var processor = _factory.CreateBuilder()
            .WithLanguage("ru")
            .Build();

        using var audio = File.OpenRead(_tempWavPath);

        var text = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(audio))
            text.Append(segment.Text);

        return text.ToString().Trim();
    }

    public void Dispose()
    {
        _waveIn?.Dispose();
        _writer?.Dispose();
        _factory?.Dispose();

        try
        {
            if (File.Exists(_tempWavPath))
                File.Delete(_tempWavPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file — not worth failing shutdown over.
        }
    }
}
