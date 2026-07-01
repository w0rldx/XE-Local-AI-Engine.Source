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
    ///     Resets every document whose <c>embedding_model</c> differs from the current model to
    ///     <see cref="KnowledgeDocumentStatus.Pending" /> and returns their ids, so the caller can enqueue a corpus-wide
    ///     reindex that rebuilds only the stale-model documents.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResetStaleDocumentsToPendingAsync(CancellationToken cancellationToken);
}

/// <summary>
///     Management summary of one knowledge-base document. <see cref="DisplayName" /> is the decrypted original file name
///     (owner-only, over the authenticated management surface). <see cref="StaleModel" /> is <see langword="true" /> when
///     the document was embedded with a model other than the currently configured one, so the UI can offer a reindex.
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
