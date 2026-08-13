namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deterministic output of splitting a structured document into retrieval units: the ordered section list plus the
///     ordered chunk list. Each chunk references the ordinal of its owning section so the index writer can link a chunk
///     row to its persisted section GUID.
/// </summary>
/// <param name="Sections">The document's sections in document order (an implicit section 0 covers pre-heading content).</param>
/// <param name="Chunks">The document's chunks in global order.</param>
public sealed record KnowledgeChunkingResult(
    IReadOnlyList<KnowledgeChunkingSection> Sections,
    IReadOnlyList<KnowledgeChunk> Chunks);

/// <summary>
///     One structural section carved out during chunking — a heading and its body, or the implicit leading section that
///     holds content appearing before the first heading.
/// </summary>
/// <param name="Ordinal">Order of this section within the document (0-based).</param>
/// <param name="Heading">Section heading text; <see langword="null" /> for the implicit (no-heading) section.</param>
/// <param name="Level">Header level 1-6; <see langword="null" /> for the implicit section.</param>
public sealed record KnowledgeChunkingSection(
    int Ordinal,
    string? Heading,
    int? Level,
    int? PageNumber = null);

/// <summary>
///     One retrieval chunk. <see cref="Content" /> is the searched/stored plaintext; <see cref="ContextualContent" /> is
///     the same text prefixed with its heading trail (embedded for retrieval, never stored as the chunk content).
/// </summary>
/// <param name="ChunkIndex">Global order of this chunk within the document (0-based); the neighbor-expansion key.</param>
/// <param name="SectionOrdinal">Ordinal of the owning <see cref="KnowledgeChunkingSection" />.</param>
/// <param name="Content">Plaintext chunk content (stored and full-text indexed).</param>
/// <param name="ContextualContent">Heading-trail prefix followed by <see cref="Content" />; the text handed to embedding.</param>
/// <param name="HeadingPath">The "H1 &gt; H2" heading trail for this chunk; <see langword="null" /> when there is none.</param>
/// <param name="TokenCount">Deterministic token approximation of <see cref="Content" /> (weighted characters ÷ 4).</param>
public sealed record KnowledgeChunk(
    int ChunkIndex,
    int SectionOrdinal,
    string Content,
    string ContextualContent,
    string? HeadingPath,
    int TokenCount,
    int? PageNumber = null,
    int StartOffset = 0,
    int EndOffset = 0,
    string ContentKind = "text",
    string? SourcePath = null,
    string? Language = null,
    string? Symbol = null,
    string ContentHash = "",
    string EmbeddingInputHash = "");
