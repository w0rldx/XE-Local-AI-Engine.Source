namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The fully-embedded projection of one document, ready for the index writer to persist atomically: the section list,
///     the chunk list (each carrying its embedding), and the embedding model that built the vectors.
/// </summary>
public sealed class KnowledgeIndexInput
{
    /// <summary>The owning document.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Content-hash revision captured before extraction; the writer commits only while it still matches.</summary>
    public required string SourceContentHash { get; init; }

    /// <summary>Model id recorded on the document row and every vector row (the search filter key).</summary>
    public required string EmbeddingModel { get; init; }

    /// <summary>Canonical model + transform algorithm/version + width identity recorded on the document and vectors.</summary>
    public required string VectorIdentity { get; init; }

    /// <summary>Width encoded by <see cref="VectorIdentity" /> and shared by every chunk vector.</summary>
    public required int VectorDimension { get; init; }

    /// <summary>Sections in document order; the writer generates a GUID per section.</summary>
    public required IReadOnlyList<KnowledgeChunkingSection> Sections { get; init; }

    /// <summary>Chunks in global order, each with its embedding blob.</summary>
    public required IReadOnlyList<KnowledgeIndexChunk> Chunks { get; init; }

    public string ParserVersion { get; init; } = KnowledgeIndexVersions.Parser;

    public string ChunkerVersion { get; init; } = KnowledgeIndexVersions.Chunker;
}

/// <summary>
///     One chunk plus its embedding, ready to persist. The writer assigns the stable <c>chunk_id</c> GUID and links the
///     chunk to its section via <see cref="SectionOrdinal" />.
/// </summary>
public sealed class KnowledgeIndexChunk
{
    /// <summary>Global order of this chunk within the document.</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>Ordinal of the owning section in <see cref="KnowledgeIndexInput.Sections" />.</summary>
    public required int SectionOrdinal { get; init; }

    /// <summary>Plaintext chunk content (stored and full-text indexed).</summary>
    public required string Content { get; init; }

    /// <summary>The "H1 &gt; H2" heading trail; <see langword="null" /> when there is none.</summary>
    public required string? HeadingPath { get; init; }

    /// <summary>Approximate token count of <see cref="Content" />.</summary>
    public required int TokenCount { get; init; }

    /// <summary>Little-endian <c>float32</c> embedding bytes.</summary>
    public required ReadOnlyMemory<byte> Embedding { get; init; }

    /// <summary>Vector dimensionality.</summary>
    public required int Dim { get; init; }

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
