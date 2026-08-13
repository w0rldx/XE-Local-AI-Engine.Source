namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Read + light-admin surface over the <c>knowledge_documents</c> catalog for the management endpoints:
///     lists documents, reads one document's detail with its chunks, and resets a document (or every stale-model document)
///     to <see cref="KnowledgeDocumentStatus.Pending" /> for a reindex. Scoped: it reads/writes through the request-scoped
///     <c>NodeChatDbContext</c> connection. Display names are decrypted server-side for the owning operator — that is the
///     one place the encrypted <c>original_file_name</c> is revealed, and only over this authenticated surface.
/// </summary>
public interface IKnowledgeDocumentCatalogService
{
    /// <summary>Lists every document (newest first) as a management summary. Never returns chunk content.</summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Lists documents in one collection namespace.</summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(string collectionId, CancellationToken cancellationToken)
    {
        return ListAsync(cancellationToken);
    }

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
    ///     the current pipeline to <see cref="KnowledgeDocumentStatus.Pending" /> and returns its id, so the caller can
    ///     enqueue a corpus-wide reindex that rebuilds only stale documents.
    ///     Non-indexed rows carry only the upload-time placeholder model name and are never treated as stale.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResetStaleDocumentsToPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Startup-recovery reset: moves every document left in a NON-terminal status (anything other than
    ///     <see cref="KnowledgeDocumentStatus.Indexed" /> or <see cref="KnowledgeDocumentStatus.Failed" />) back to
    ///     <see cref="KnowledgeDocumentStatus.Pending" /> (clearing any partial failure reason) and returns their ids, so
    ///     the background worker can re-dispatch documents whose in-memory queue entry was lost to a crash or hard stop.
    ///     Terminal rows are left untouched.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResetNonTerminalToPendingAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Lists the ids of every document currently in <see cref="KnowledgeDocumentStatus.Pending" /> WITHOUT mutating any
    ///     row. Because the ingestion pipeline flips a document out of Pending the instant it starts, a Pending row is one
    ///     that has not begun ingestion — either freshly uploaded or stranded when a full queue rejected its admission. The
    ///     background worker uses this as its drain-sweep source to re-admit stranded documents as queue capacity frees,
    ///     without the reset semantics (and in-progress clobbering) of <see cref="ResetNonTerminalToPendingAsync" />.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListPendingDocumentIdsAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Management summary of one knowledge-base document. <see cref="DisplayName" /> is the decrypted original file name
///     (owner-only, over the authenticated management surface). <see cref="StaleModel" /> is <see langword="true" /> when
///     the document is Indexed but was embedded with a model other than the currently resolved one, so the UI can offer a
///     reindex.
/// </summary>
public sealed record KnowledgeDocumentSummary(
    Guid DocumentId,
    string DisplayName,
    KnowledgeDocumentStatus Status,
    string? FailureReason,
    int ChunkCount,
    string EmbeddingModel,
    bool StaleModel,
    long SizeBytes,
    long CreatedAtUtc,
    string CollectionId = KnowledgeCollectionScope.DefaultId,
    string? SourcePath = null,
    string SourceKind = "upload");

/// <summary>One document's full detail plus its ordered chunks, for the detail drawer.</summary>
public sealed record KnowledgeDocumentDetail(
    Guid DocumentId,
    string DisplayName,
    KnowledgeDocumentStatus Status,
    string? FailureReason,
    int ChunkCount,
    string EmbeddingModel,
    bool StaleModel,
    long SizeBytes,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    IReadOnlyList<KnowledgeDocumentChunkView> Chunks,
    string CollectionId = KnowledgeCollectionScope.DefaultId,
    string? SourcePath = null,
    string SourceKind = "upload");

/// <summary>A single chunk view for the detail drawer: its global order, heading trail, and plaintext content.</summary>
public sealed record KnowledgeDocumentChunkView(
    int ChunkIndex,
    string? HeadingPath,
    string Content,
    int? PageNumber = null,
    int StartOffset = 0,
    int EndOffset = 0,
    string ContentKind = "text",
    string? SourcePath = null,
    string? Language = null,
    string? Symbol = null);
