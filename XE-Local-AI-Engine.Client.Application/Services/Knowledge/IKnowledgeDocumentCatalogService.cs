namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Read + light-admin surface over the <c>knowledge_documents</c> catalog for the management endpoints (Lane D):
///     lists documents, reads one document's detail with its chunks, and resets a document (or every stale-model document)
///     to <see cref="KnowledgeDocumentStatus.Pending" /> for a reindex. Scoped: it reads/writes through the request-scoped
///     <c>NodeChatDbContext</c> connection. Display names are decrypted server-side for the owning operator — that is the
///     one place the encrypted <c>original_file_name</c> is revealed, and only over this authenticated surface.
/// </summary>
public interface IKnowledgeDocumentCatalogService
{
    /// <summary>Lists every document (newest first) as a management summary. Never returns chunk content.</summary>
    Task<IReadOnlyList<KnowledgeDocumentSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Reads one document's detail plus its ordered chunks, or <see langword="null" /> when the id is unknown.</summary>
    Task<KnowledgeDocumentDetail?> GetAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Reads one document's current pipeline status, or <see langword="null" /> when the id is unknown.</summary>
    Task<KnowledgeDocumentStatus?> GetStatusAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    ///     Resets one document to <see cref="KnowledgeDocumentStatus.Pending" /> (clearing any failure reason) so a
    ///     reindex can re-run the pipeline. Returns <see langword="false" /> when the id is unknown.
    /// </summary>
    Task<bool> ResetToPendingAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    ///     Resets every INDEXED document whose stored <c>embedding_model</c> differs from the currently RESOLVED embedding
    ///     model (from <see cref="IEmbeddingModelResolver" />) to <see cref="KnowledgeDocumentStatus.Pending" /> and returns
    ///     their ids, so the caller can enqueue a corpus-wide reindex that rebuilds only the stale-model documents.
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
    long CreatedAtUtc);

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
    IReadOnlyList<KnowledgeDocumentChunkView> Chunks);

/// <summary>A single chunk view for the detail drawer: its global order, heading trail, and plaintext content.</summary>
public sealed record KnowledgeDocumentChunkView(int ChunkIndex, string? HeadingPath, string Content);
