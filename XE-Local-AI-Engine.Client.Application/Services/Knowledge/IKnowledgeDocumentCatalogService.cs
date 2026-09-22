namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Read + light-admin surface over the <c>knowledge_documents</c> catalog for the management endpoints: lists
///     documents, reads one document's detail with its chunks, and resets documents to
///     <see cref="KnowledgeDocumentStatus.Pending" /> for a reindex.
/// </summary>
/// <remarks>
///     Scoped: reads and writes through the request-scoped <c>NodeChatDbContext</c> connection. Display names are
///     decrypted server-side for the owning operator — the one place the encrypted <c>original_file_name</c> is
///     revealed, and only over this authenticated surface.
/// </remarks>
public interface IKnowledgeDocumentCatalogService
{
    /// <summary>Lists every document (newest first) as a management summary. Never returns chunk content.</summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Lists documents in one collection namespace.</summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(string collectionId, CancellationToken cancellationToken)
    {
        return ListAsync(cancellationToken);
    }

    /// <summary>
    ///     Lists documents (optionally in one collection) together with the embedding-model resolution the ingestion
    ///     lane would use right now, so a caller can gate uploads on the same truth ingestion runs on.
    /// </summary>
    Task<KnowledgeDocumentListing> ListWithEmbeddingStatusAsync(string? collectionId, CancellationToken cancellationToken);

    /// <summary>Lists documents belonging to one durable source inside a collection.</summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(string collectionId,
        string sourceKind,
        string sourceId,
        CancellationToken cancellationToken);

    /// <summary>Reads one document's detail plus its ordered chunks, or <see langword="null" /> when the id is unknown.</summary>
    Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads one document only when it belongs to <paramref name="collectionId" />. This is the authorization seam
    ///     used by agent follow-up reads so possession of a document id never bypasses its collection namespace.
    /// </summary>
    Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, string collectionId, CancellationToken cancellationToken);

    /// <summary>Reads one document's current pipeline status, or <see langword="null" /> when the id is unknown.</summary>
    Task<KnowledgeDocumentStatus?> GetStatusAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    ///     Resets one document to <see cref="KnowledgeDocumentStatus.Pending" /> (clearing any failure reason) so a
    ///     reindex can re-run the pipeline. Returns <see langword="false" /> when the id is unknown.
    /// </summary>
    Task<bool> ResetToPendingAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    ///     Resets every INDEXED document whose stored embedding/vector identity or parser/chunker version differs from
    ///     the current pipeline to <see cref="KnowledgeDocumentStatus.Pending" /> and returns its id.
    /// </summary>
    /// <remarks>
    ///     The caller enqueues a corpus-wide reindex that rebuilds only stale documents. Non-indexed rows carry only the
    ///     upload-time placeholder model name and are never treated as stale.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ResetStaleDocumentsToPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Startup-recovery reset: moves every document left in a NON-terminal status back to
    ///     <see cref="KnowledgeDocumentStatus.Pending" />, clearing any partial failure reason, and returns their ids.
    /// </summary>
    /// <remarks>
    ///     Non-terminal means anything other than <see cref="KnowledgeDocumentStatus.Indexed" /> or
    ///     <see cref="KnowledgeDocumentStatus.Failed" />; terminal rows are left untouched. The background worker then
    ///     re-dispatches documents whose in-memory queue entry was lost to a crash or hard stop.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ResetNonTerminalToPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Lists the ids of every document currently in <see cref="KnowledgeDocumentStatus.Pending" /> WITHOUT mutating
    ///     any row.
    /// </summary>
    /// <remarks>
    ///     The pipeline flips a document out of Pending the instant ingestion starts, so a Pending row has not begun
    ///     ingestion — freshly uploaded, or stranded when a full queue rejected its admission. The background worker uses
    ///     this as its drain-sweep source to re-admit stranded documents as queue capacity frees, without the reset
    ///     semantics — and in-progress clobbering — of <see cref="ResetNonTerminalToPendingAsync" />.
    /// </remarks>
    Task<IReadOnlyList<Guid>> ListPendingDocumentIdsAsync(CancellationToken cancellationToken);
}

/// <summary>Documents plus the embedding model ingestion would embed them with right now.</summary>
/// <remarks>
///     <see cref="EmbeddingModelResolution.IsConfident" /> is the upload precondition: a NOT-confident resolution means
///     no installed model was matched, so an accepted upload would fail later in the background embedder.
/// </remarks>
public sealed class KnowledgeDocumentListing
{
    public required IReadOnlyList<KnowledgeDocumentSummary> Items { get; init; }

    public required EmbeddingModelResolution Embedding { get; init; }
}

/// <summary>Management summary of one knowledge-base document.</summary>
/// <remarks>
///     <see cref="DisplayName" /> is the decrypted original file name, owner-only over the authenticated management
///     surface. <see cref="StaleModel" /> is <see langword="true" /> when the document is Indexed but was embedded with
///     a model other than the currently resolved one, so the UI can offer a reindex.
/// </remarks>
public sealed class KnowledgeDocumentSummary
{
    public required Guid DocumentId { get; init; }

    public required string DisplayName { get; init; }

    public required KnowledgeDocumentStatus Status { get; init; }

    public required string? FailureReason { get; init; }

    public required int ChunkCount { get; init; }

    public required string EmbeddingModel { get; init; }

    public required bool StaleModel { get; init; }

    public required long SizeBytes { get; init; }

    public required long CreatedAtUtc { get; init; }

    public string CollectionId { get; init; } = KnowledgeCollectionScope.DefaultId;

    public string? SourcePath { get; init; }

    public string SourceKind { get; init; } = "upload";
}

/// <summary>One document's full detail plus its ordered chunks, for the detail drawer.</summary>
public sealed record KnowledgeDocumentDetail
{
    public required Guid DocumentId { get; init; }

    public required string DisplayName { get; init; }

    public required KnowledgeDocumentStatus Status { get; init; }

    public required string? FailureReason { get; init; }

    public required int ChunkCount { get; init; }

    public required string EmbeddingModel { get; init; }

    public required bool StaleModel { get; init; }

    public required long SizeBytes { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required IReadOnlyList<KnowledgeDocumentChunkView> Chunks { get; init; }

    public string CollectionId { get; init; } = KnowledgeCollectionScope.DefaultId;

    public string? SourcePath { get; init; }

    public string SourceKind { get; init; } = "upload";
}

/// <summary>A single chunk view for the detail drawer: its global order, heading trail, and plaintext content.</summary>
public sealed class KnowledgeDocumentChunkView
{
    public required int ChunkIndex { get; init; }

    public required string? HeadingPath { get; init; }

    public required string Content { get; init; }

    public int? PageNumber { get; init; }

    public int StartOffset { get; init; }

    public int EndOffset { get; init; }

    public string ContentKind { get; init; } = "text";

    public string? SourcePath { get; init; }

    public string? Language { get; init; }

    public string? Symbol { get; init; }
}
