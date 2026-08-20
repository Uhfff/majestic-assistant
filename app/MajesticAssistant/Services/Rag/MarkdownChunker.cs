using System.IO;
using System.Security.Cryptography;
using System.Text;
using MajesticAssistant.Models;

namespace MajesticAssistant.Services.Rag;

/// <summary>
/// Turns every markdown file under the knowledge-base folder into a set of <see cref="KnowledgeChunk"/>s
/// small enough to embed and retrieve individually. Splits on markdown headings first (the kb files
/// are already organized as "## Глава" / "### Раздел" with numbered "Статья"/"Пункт" entries inside),
/// then further splits any section that's still too large at paragraph boundaries — this keeps a
/// chunk's own heading trail attached to it, so retrieval doesn't return an orphaned paragraph with
/// no idea which law or rule document it came from.
/// </summary>
public static class MarkdownChunker
{
    // Keeps chunks small enough for the embedding model's context and for several of them to fit
    // comfortably alongside the question in the generation prompt, while still holding a handful
    // of complete "Статья ..." entries together instead of splitting mid-thought.
    private const int MaxChunkChars = 1400;

    public static List<KnowledgeChunk> ChunkDirectory(string kbRoot)
    {
        var chunks = new List<KnowledgeChunk>();
        if (!Directory.Exists(kbRoot))
            return chunks;

        foreach (var file in Directory.EnumerateFiles(kbRoot, "*.md", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(kbRoot, file).Replace('\\', '/');
            var raw = File.ReadAllText(file);
            var (frontMatter, body) = SplitFrontMatter(raw);

            var title = frontMatter.GetValueOrDefault("title", Path.GetFileNameWithoutExtension(file));
            var category = frontMatter.GetValueOrDefault("category", "");
            var sourceUrl = frontMatter.GetValueOrDefault("source");

            chunks.AddRange(ChunkBody(body, relativePath, title, category, sourceUrl));
        }

        return chunks;
    }

    private static (Dictionary<string, string> FrontMatter, string Body) SplitFrontMatter(string raw)
    {
        var map = new Dictionary<string, string>();
        if (!raw.StartsWith("---"))
            return (map, raw);

        var end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
            return (map, raw);

        var header = raw[3..end];
        var body = raw[(end + 4)..].TrimStart('\n');

        foreach (var line in header.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (key.Length > 0 && value.Length > 0)
                map[key] = value;
        }

        return (map, body);
    }

    private static IEnumerable<KnowledgeChunk> ChunkBody(
        string body, string sourceFile, string title, string category, string? sourceUrl)
    {
        var lines = body.Split('\n');
        var sections = new List<(string HeadingPath, StringBuilder Text)>();
        var headingStack = new List<string>();
        StringBuilder current = new();
        sections.Add(("", current));

        foreach (var line in lines)
        {
            var headingLevel = CountLeadingHashes(line);
            if (headingLevel is >= 1 and <= 4)
            {
                var headingText = line[(headingLevel + 1)..].Trim();
                // Truncate the stack to this level, then push the new heading — e.g. seeing an
                // "##" after "### Раздел 1" replaces the "###" entry, giving a clean breadcrumb.
                while (headingStack.Count >= headingLevel)
                    headingStack.RemoveAt(headingStack.Count - 1);
                headingStack.Add(headingText);

                current = new StringBuilder();
                sections.Add((string.Join(" → ", headingStack), current));
                continue;
            }

            current.AppendLine(line);
        }

        foreach (var (headingPath, text) in sections)
        {
            var sectionText = text.ToString().Trim();
            if (sectionText.Length == 0)
                continue;

            foreach (var piece in SplitToSize(sectionText, MaxChunkChars))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length == 0)
                    continue;

                yield return new KnowledgeChunk
                {
                    SourceFile = sourceFile,
                    Title = title,
                    Category = category,
                    SourceUrl = sourceUrl,
                    HeadingPath = headingPath,
                    Text = trimmed,
                    ChunkId = ComputeChunkId(sourceFile, headingPath, trimmed),
                };
            }
        }
    }

    private static int CountLeadingHashes(string line)
    {
        var i = 0;
        while (i < line.Length && line[i] == '#') i++;
        return i > 0 && i < line.Length && line[i] == ' ' ? i : 0;
    }

    /// <summary>Splits on blank-line paragraph boundaries, packing consecutive paragraphs together
    /// until the next one would push a piece over the size limit — never cuts a paragraph in half.</summary>
    private static IEnumerable<string> SplitToSize(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            yield return text;
            yield break;
        }

        var paragraphs = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var buffer = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (buffer.Length > 0 && buffer.Length + paragraph.Length + 2 > maxChars)
            {
                yield return buffer.ToString();
                buffer.Clear();
            }

            if (paragraph.Length > maxChars)
            {
                // A single paragraph longer than the limit (rare — e.g. a dense list) — hard-split
                // it rather than dropping it, so nothing from the source is silently lost.
                if (buffer.Length > 0)
                {
                    yield return buffer.ToString();
                    buffer.Clear();
                }
                for (var i = 0; i < paragraph.Length; i += maxChars)
                    yield return paragraph.Substring(i, Math.Min(maxChars, paragraph.Length - i));
                continue;
            }

            if (buffer.Length > 0) buffer.Append("\n\n");
            buffer.Append(paragraph);
        }

        if (buffer.Length > 0)
            yield return buffer.ToString();
    }

    private static string ComputeChunkId(string sourceFile, string headingPath, string text)
    {
        var input = $"{sourceFile}|{headingPath}|{text}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }
}
