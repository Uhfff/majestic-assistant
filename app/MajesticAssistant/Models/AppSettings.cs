using System.Text.Json.Serialization;

namespace MajesticAssistant.Models;

/// <summary>
/// Everything about the app that should survive a restart. There's no in-app settings UI yet —
/// these are written automatically (window position, on exit) or by hand-editing the JSON file
/// (model names, before launching) — see the README for the manual-edit workflow.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("windowLeft")]
    public double? WindowLeft { get; set; }

    [JsonPropertyName("windowTop")]
    public double? WindowTop { get; set; }

    /// <summary>Overrides <see cref="Services.Rag.RagService.EmbedModel"/> when set; null keeps the built-in default.</summary>
    [JsonPropertyName("embedModel")]
    public string? EmbedModel { get; set; }

    /// <summary>Overrides <see cref="Services.Rag.RagService.ChatModel"/> when set; null keeps the built-in default.</summary>
    [JsonPropertyName("chatModel")]
    public string? ChatModel { get; set; }

    /// <summary>Filename (not a full path) of the whisper.cpp GGML model under whisper/, e.g.
    /// "ggml-medium.bin". Null keeps the built-in default of "ggml-small.bin" — see README.</summary>
    [JsonPropertyName("whisperModel")]
    public string? WhisperModel { get; set; }
}
