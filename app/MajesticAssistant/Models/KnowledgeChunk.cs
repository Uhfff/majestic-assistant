namespace MajesticAssistant.Models;

/// <summary>
/// One retrievable slice of the knowledge base — a section (or a size-bounded piece of a large
/// section) from one of the markdown files under <c>data/kb</c>. <see cref="HeadingPath"/> is
/// kept alongside the raw text so a citation like "Закон «О государственных территориях» →
/// Статья 5" can be shown next to an answer instead of just a bare paragraph.
/// </summary>
public sealed record KnowledgeChunk
{
    public required string SourceFile { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public string? SourceUrl { get; init; }
    public required string HeadingPath { get; init; }
    public required string Text { get; init; }

    /// <summary>Stable identity for cache invalidation — content hash, not a random GUID,
    /// so the embedding cache survives across app restarts as long as the text is unchanged.</summary>
    public required string ChunkId { get; init; }
}
