using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MajesticAssistant.Models;

namespace MajesticAssistant.Services.Rag;

/// <summary>
/// Ties the chunker, embedding index and Ollama client together into the two operations the UI
/// actually needs: build the index once at startup, then answer questions against it. Everything
/// runs against a local Ollama instance — no data ever leaves the player's machine.
/// </summary>
public sealed class RagService
{
    private readonly OllamaClient _ollama;
    private readonly EmbeddingIndex _index = new();
    private readonly string _kbRoot;
    private readonly string _cachePath;

    /// <summary>Pull with <c>ollama pull nomic-embed-text</c> — a small, fast embedding-only model.</summary>
    public string EmbedModel { get; set; } = "nomic-embed-text";

    /// <summary>Pull with <c>ollama pull qwen2.5:7b</c> (or swap for another instruction-tuned model
    /// that fits the player's GPU/RAM — see the README for alternatives).</summary>
    public string ChatModel { get; set; } = "qwen2.5:7b";

    public bool IsReady { get; private set; }

    /// <summary>
    /// Players abbreviate document names when citing an article ("15.2 УК", "п.3 ПК") — cosine
    /// similarity alone often can't tell those short, number-heavy questions apart from an
    /// identically-numbered clause in a completely different document (e.g. a "15.2" clause in the
    /// EMS statute outscoring the real "15.2 УК" match). <see cref="RerankByTitleHints"/> gives a
    /// flat score boost to candidates whose title matches a recognized abbreviation, which is
    /// enough to break that kind of near-tie without overriding a genuinely stronger semantic match
    /// for questions that don't mention any of these.
    /// </summary>
    private static readonly (string Abbreviation, string TitleHint)[] KnownAbbreviations =
    {
        ("УК", "Уголовный кодекс"),
        ("АК", "Административный кодекс"),
        ("ДК", "Дорожный кодекс"),
        ("ЭК", "Этический кодекс"),
        ("ТК", "Трудовой кодекс"),
        ("ПК", "Процессуальный кодекс"),
        ("ИК", "Избирательный кодекс"),
        ("ЛСПД", "LSPD"),
        ("LSPD", "LSPD"),
        ("ЕМС", "Emergency Medical Service"),
        ("EMS", "Emergency Medical Service"),
        ("САНГ", "San Andreas National Guard"),
        ("SANG", "San Andreas National Guard"),
        ("ФИБ", "Federal Investigation Bureau"),
        ("FIB", "Federal Investigation Bureau"),
    };

    private const double AbbreviationBoost = 0.15;

    public RagService(string kbRoot, string cachePath, string baseUrl = "http://localhost:11434")
    {
        _kbRoot = kbRoot;
        _cachePath = cachePath;
        _ollama = new OllamaClient(baseUrl);
    }

    /// <summary>
    /// Loads/embeds the knowledge base. Safe to call on a background thread — it does real network
    /// and disk I/O and, on a cold cache, can take a couple of minutes for the whole kb.
    /// </summary>
    public async Task InitializeAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        onStatus?.Invoke("Проверяю подключение к Ollama…");
        if (!await _ollama.IsReachableAsync(ct))
        {
            throw new InvalidOperationException(
                "Ollama не запущена. Установи её с ollama.com, выполни \"ollama pull " +
                $"{EmbedModel}\" и \"ollama pull {ChatModel}\", затем перезапусти ассистента.");
        }

        onStatus?.Invoke("Читаю базу знаний…");
        var chunks = MarkdownChunker.ChunkDirectory(_kbRoot);
        if (chunks.Count == 0)
            throw new InvalidOperationException($"База знаний не найдена: {_kbRoot}");

        await _index.LoadOrBuildAsync(chunks, _ollama, EmbedModel, _cachePath,
            (done, total) =>
            {
                if (total > 0) onStatus?.Invoke($"Индексирую базу знаний… {done}/{total}");
            }, ct);

        IsReady = true;
    }

    /// <summary>
    /// Embeds the question, retrieves the top matching kb chunks, and streams a generated answer
    /// grounded in them via <paramref name="onToken"/>.
    /// </summary>
    public async Task AskAsync(string question, Action<string> onToken, int topK = 6, CancellationToken ct = default)
    {
        if (!IsReady)
            throw new InvalidOperationException("RagService.InitializeAsync must complete before AskAsync.");

        var queryVector = await _ollama.EmbedAsync(EmbedModel, question, ct);

        // Pull a wider candidate pool than we'll actually use — the reranking step below needs
        // room to pull a correctly-titled chunk up from, say, 9th place into the final top K.
        var candidates = _index.Search(queryVector, topK * 4);
        var matches = RerankByTitleHints(question, candidates).Take(topK).ToList();

        if (matches.Count == 0 || matches[0].Score < 0.2)
        {
            onToken("Не нашёл в базе знаний ничего похожего на этот вопрос. Попробуй переформулировать " +
                     "или уточнить, о каком правиле/законе идёт речь.");
            return;
        }

        var prompt = BuildPrompt(question, matches);
        await _ollama.GenerateStreamingAsync(ChatModel, prompt, onToken, ct);
    }

    private static IEnumerable<(KnowledgeChunk Chunk, double Score)> RerankByTitleHints(
        string question, IReadOnlyList<(KnowledgeChunk Chunk, double Score)> candidates)
    {
        var hints = KnownAbbreviations
            .Where(a => Regex.IsMatch(question, $@"\b{Regex.Escape(a.Abbreviation)}\b", RegexOptions.IgnoreCase))
            .Select(a => a.TitleHint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (hints.Count == 0)
            return candidates;

        return candidates
            .Select(m => hints.Any(h => m.Chunk.Title.Contains(h, StringComparison.OrdinalIgnoreCase))
                ? (m.Chunk, Score: m.Score + AbbreviationBoost)
                : m)
            .OrderByDescending(m => m.Score);
    }

    private static string BuildPrompt(string question, IReadOnlyList<(KnowledgeChunk Chunk, double Score)> matches)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Ты — игровой ассистент сервера Majestic RP (GTA V RolePlay). Ты отвечаешь на " +
                       "вопросы игрока по правилам сервера, законам штата и уставам организаций.");
        sb.AppendLine("Правила ответа:");
        sb.AppendLine("- Отвечай кратко, по делу, на русском языке.");
        sb.AppendLine("- Опирайся ТОЛЬКО на фрагменты базы знаний ниже — не придумывай статьи, номера или суммы штрафов.");
        sb.AppendLine("- Если в фрагментах нет точного ответа, честно скажи об этом, не выдумывай.");
        sb.AppendLine("- Если есть номер статьи/пункта и наказание — обязательно укажи их в ответе.");
        sb.AppendLine("- В конце коротко укажи источник в формате [Источник: Название документа].");
        sb.AppendLine();
        sb.AppendLine("=== Фрагменты базы знаний ===");

        foreach (var (chunk, _) in matches)
        {
            sb.AppendLine($"--- {chunk.Title}{(string.IsNullOrEmpty(chunk.HeadingPath) ? "" : " — " + chunk.HeadingPath)} ---");
            sb.AppendLine(chunk.Text);
            sb.AppendLine();
        }

        sb.AppendLine("=== Конец фрагментов ===");
        sb.AppendLine();
        sb.AppendLine($"Вопрос игрока: {question}");
        sb.AppendLine("Ответ:");

        return sb.ToString();
    }
}
