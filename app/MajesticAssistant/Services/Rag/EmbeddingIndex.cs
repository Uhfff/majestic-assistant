using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MajesticAssistant.Models;

namespace MajesticAssistant.Services.Rag;

/// <summary>
/// In-memory vector index over the knowledge base, backed by a JSON cache file so the app doesn't
/// have to re-embed every chunk (there are several hundred across data/kb) on every single launch —
/// only chunks whose content actually changed since the last run get sent to Ollama again, keyed by
/// <see cref="KnowledgeChunk.ChunkId"/> (a content hash, not a random id).
/// </summary>
public sealed class EmbeddingIndex
{
    private sealed record CacheEntry(
        [property: JsonPropertyName("chunkId")] string ChunkId,
        [property: JsonPropertyName("vector")] float[] Vector);

    private readonly List<(KnowledgeChunk Chunk, float[] Vector)> _entries = new();

    public int Count => _entries.Count;

    /// <summary>
    /// Embeds every chunk that isn't already in the on-disk cache, reusing cached vectors for the
    /// rest, then writes the merged cache back out. <paramref name="onProgress"/> reports
    /// (embedded-so-far, total-needing-embedding) so the UI can show real progress on a cold start
    /// instead of a plain spinner — the first run over the whole kb can take a couple of minutes.
    /// </summary>
    public async Task LoadOrBuildAsync(
        IReadOnlyList<KnowledgeChunk> chunks,
        OllamaClient client,
        string embedModel,
        string cachePath,
        Action<int, int>? onProgress = null,
        CancellationToken ct = default)
    {
        var cached = LoadCache(cachePath);

        var toEmbed = chunks.Where(c => !cached.ContainsKey(c.ChunkId)).ToList();
        var total = toEmbed.Count;
        var done = 0;
        onProgress?.Invoke(done, total);

        if (total > 0)
        {
            // Embedding each chunk is one HTTP round-trip to Ollama; doing them one at a time on a
            // cold cache (hundreds of chunks) spends most of the wall-clock time just waiting on
            // network/request overhead rather than the model itself. Ollama can serve several
            // embedding requests at once, so a small bounded parallelism cuts the one-time indexing
            // time substantially without flooding it with hundreds of simultaneous requests.
            const int maxConcurrency = 4;
            using var gate = new SemaphoreSlim(maxConcurrency);
            var results = new ConcurrentDictionary<string, float[]>();

            var tasks = toEmbed.Select(async chunk =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var vector = await client.EmbedAsync(embedModel, BuildEmbeddingText(chunk), ct);
                    results[chunk.ChunkId] = vector;
                }
                finally
                {
                    gate.Release();
                }

                onProgress?.Invoke(Interlocked.Increment(ref done), total);
            });

            await Task.WhenAll(tasks);

            foreach (var (chunkId, vector) in results)
                cached[chunkId] = vector;
        }

        _entries.Clear();
        foreach (var chunk in chunks)
        {
            if (cached.TryGetValue(chunk.ChunkId, out var vector))
                _entries.Add((chunk, vector));
        }

        // Persist only vectors for chunks that still exist — prunes entries for kb files/sections
        // that were edited or removed since the cache was last written.
        if (toEmbed.Count > 0)
            SaveCache(cachePath, chunks, cached);
    }

    /// <summary>Prepend the heading trail so the embedding captures context a bare paragraph would
    /// lose — e.g. distinguishing "Штраф до 5000$" under Дорожный Кодекс from an identical-looking
    /// line elsewhere.</summary>
    private static string BuildEmbeddingText(KnowledgeChunk chunk) =>
        string.IsNullOrEmpty(chunk.HeadingPath)
            ? $"{chunk.Title}\n{chunk.Text}"
            : $"{chunk.Title} — {chunk.HeadingPath}\n{chunk.Text}";

    public IReadOnlyList<(KnowledgeChunk Chunk, double Score)> Search(float[] queryVector, int topK)
    {
        return _entries
            .Select(e => (e.Chunk, Score: CosineSimilarity(queryVector, e.Vector)))
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        if (magA == 0 || magB == 0) return 0;
        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private static Dictionary<string, float[]> LoadCache(string cachePath)
    {
        if (!File.Exists(cachePath))
            return new Dictionary<string, float[]>();

        try
        {
            var json = File.ReadAllText(cachePath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json) ?? new();
            return entries.ToDictionary(e => e.ChunkId, e => e.Vector);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Corrupt or unreadable cache — treat as empty rather than crash startup; everything
            // just gets re-embedded once.
            return new Dictionary<string, float[]>();
        }
    }

    private static void SaveCache(string cachePath, IReadOnlyList<KnowledgeChunk> chunks, Dictionary<string, float[]> cached)
    {
        var liveIds = chunks.Select(c => c.ChunkId).ToHashSet();
        var toSave = cached
            .Where(kv => liveIds.Contains(kv.Key))
            .Select(kv => new CacheEntry(kv.Key, kv.Value))
            .ToList();

        var dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(cachePath, JsonSerializer.Serialize(toSave));
    }
}
