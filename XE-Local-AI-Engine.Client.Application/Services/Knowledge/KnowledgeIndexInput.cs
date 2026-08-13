namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The fully-embedded projection of one document, ready for the index writer to persist atomically: the section list,
///     the chunk list (each carrying its embedding), and the embedding model that built the vectors.
/// </summary>
/// <param name="DocumentId">The owning document.</param>
/// <param name="SourceContentHash">
///     Content-hash revision captured before extraction; the writer commits only while it still matches.
/// </param>
/// <param name="EmbeddingModel">Model id recorded on the document row and every vector row (the search filter key).</param>
/// <param name="VectorIdentity">Canonical model + transform algorithm/version + width identity recorded on the document and vectors.</param>
/// <param name="VectorDimension">Width encoded by <paramref name="VectorIdentity" /> and shared by every chunk vector.</param>
/// <param name="Sections">Sections in document order; the writer generates a GUID per section.</param>
/// <param name="Chunks">Chunks in global order, each with its embedding blob.</param>
public sealed record KnowledgeIndexInput(
    Guid DocumentId,
    string SourceContentHash,
    string EmbeddingModel,
    string VectorIdentity,
    int VectorDimension,
    IReadOnlyList<KnowledgeChunkingSection> Sections,
    IReadOnlyList<KnowledgeIndexChunk> Chunks,
    string ParserVersion = KnowledgeIndexVersions.Parser,
    string ChunkerVersion = KnowledgeIndexVersions.Chunker);

/// <summary>
///     One chunk plus its embedding, ready to persist. The writer assigns the stable <c>chunk_id</c> GUID and links the
///     chunk to its section via <see cref="SectionOrdinal" />.
/// </summary>
/// <param name="ChunkIndex">Global order of this chunk within the document.</param>
/// <param name="SectionOrdinal">Ordinal of the owning section in <see cref="KnowledgeIndexInput.Sections" />.</param>
/// <param name="Content">Plaintext chunk content (stored and full-text indexed).</param>
/// <param name="HeadingPath">The "H1 &gt; H2" heading trail; <see langword="null" /> when there is none.</param>
/// <param name="TokenCount">Approximate token count of <see cref="Content" />.</param>
/// <param name="Embedding">Little-endian <c>float32</c> embedding bytes.</param>
/// <param name="Dim">Vector dimensionality.</param>
public sealed record KnowledgeIndexChunk(
    int ChunkIndex,
    int SectionOrdinal,
    string Content,
    string? HeadingPath,
    int TokenCount,
    ReadOnlyMemory<byte> Embedding,
    int Dim,
    int? PageNumber = null,
    int StartOffset = 0,
    int EndOffset = 0,
    string ContentKind = "text",
    string? SourcePath = null,
    string? Language = null,
    string? Symbol = null,
    string ContentHash = "",
    string EmbeddingInputHash = "");
