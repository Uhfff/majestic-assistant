using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace MajesticAssistant.Services.Rag;

/// <summary>
/// Thin wrapper over Ollama's local REST API (default <c>http://localhost:11434</c>). Ollama must
/// be installed and running on the user's machine with the embedding and chat models already
/// pulled — see the README for the exact <c>ollama pull</c> commands. This class assumes nothing
/// about which models are installed; the caller supplies model names.
/// </summary>
public sealed class OllamaClient(string baseUrl = "http://localhost:11434")
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };

    private sealed record EmbeddingRequest([property: JsonPropertyName("model")] string Model,
                                            [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record EmbeddingResponse([property: JsonPropertyName("embedding")] float[] Embedding);

    public async Task<float[]> EmbedAsync(string model, string text, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/embeddings",
            new EmbeddingRequest(model, text), ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);
        return body?.Embedding ?? throw new InvalidOperationException("Ollama returned no embedding.");
    }

    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record GenerateChunk(
        [property: JsonPropertyName("response")] string? Response,
        [property: JsonPropertyName("done")] bool Done);

    /// <summary>
    /// Streams the model's answer token-by-token via <paramref name="onToken"/> as it's generated,
    /// so the UI can show text arriving live instead of freezing until the whole answer is ready —
    /// matches the "never appear instantly" motion guidance the overlay's design already follows.
    /// </summary>
    public async Task GenerateStreamingAsync(
        string model, string prompt, Action<string> onToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = JsonContent.Create(new GenerateRequest(model, prompt, Stream: true)),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            GenerateChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<GenerateChunk>(line);
            }
            catch (JsonException)
            {
                continue; // a malformed/partial line — skip rather than crash the whole answer
            }

            if (chunk is { Response.Length: > 0 })
                onToken(chunk.Response);

            if (chunk?.Done == true)
                break;
        }
    }

    /// <summary>Quick reachability check so the UI can show a clear "Ollama не запущен" message
    /// instead of a generic error the first time something's misconfigured.</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
