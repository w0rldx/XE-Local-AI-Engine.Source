namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deterministic output of splitting a structured document into retrieval units: the ordered section list plus the
///     ordered chunk list. Each chunk references the ordinal of its owning section so the index writer can link a chunk
///     row to its persisted section GUID.
/// </summary>
public sealed record KnowledgeChunkingResult
{
    /// <summary>The document's sections in document order (an implicit section 0 covers pre-heading content).</summary>
    public required IReadOnlyList<KnowledgeChunkingSection> Sections { get; init; }

    /// <summary>The document's chunks in global order.</summary>
    public required IReadOnlyList<KnowledgeChunk> Chunks { get; init; }
}

/// <summary>
///     One structural section carved out during chunking — a heading and its body, or the implicit leading section that
///     holds content appearing before the first heading.
/// </summary>
public sealed class KnowledgeChunkingSection
{
    /// <summary>Order of this section within the document (0-based).</summary>
    public required int Ordinal { get; init; }

    /// <summary>Section heading text; <see langword="null" /> for the implicit (no-heading) section.</summary>
    public required string? Heading { get; init; }

    /// <summary>Header level 1-6; <see langword="null" /> for the implicit section.</summary>
    public required int? Level { get; init; }

    public int? PageNumber { get; init; }
}

/// <summary>
///     One retrieval chunk. <see cref="Content" /> is the searched/stored plaintext; <see cref="ContextualContent" /> is
///     the same text prefixed with its heading trail (embedded for retrieval, never stored as the chunk content).
/// </summary>
public sealed record KnowledgeChunk
{
    /// <summary>Global order of this chunk within the document (0-based); the neighbor-expansion key.</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>Ordinal of the owning <see cref="KnowledgeChunkingSection" />.</summary>
    public required int SectionOrdinal { get; init; }

    /// <summary>Plaintext chunk content (stored and full-text indexed).</summary>
    public required string Content { get; init; }

    /// <summary>Heading-trail prefix followed by <see cref="Content" />; the text handed to embedding.</summary>
    public required string ContextualContent { get; init; }

    /// <summary>The "H1 &gt; H2" heading trail for this chunk; <see langword="null" /> when there is none.</summary>
    public required string? HeadingPath { get; init; }

    /// <summary>Deterministic token approximation of <see cref="Content" /> (weighted characters ÷ 4).</summary>
    public required int TokenCount { get; init; }

    public int? PageNumber { get; init; }

    public int StartOffset { get; init; }

    public int EndOffset { get; init; }

    public string ContentKind { get; init; } = "text";

    public string? SourcePath { get; init; }

    public string? Language { get; init; }

    public string? Symbol { get; init; }

    public string ContentHash { get; init; } = "";

    public string EmbeddingInputHash { get; init; } = "";
}
