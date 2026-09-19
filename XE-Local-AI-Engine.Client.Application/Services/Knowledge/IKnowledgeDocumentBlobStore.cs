namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Durable store for a knowledge-base document's source-of-truth row plus its encrypted raw bytes. The bytes live
///     encrypted on disk under <c>INodeDataDirectory.Root/knowledge-base/documents/</c>; the metadata (with an encrypted
///     display name) lives in the <c>knowledge_documents</c> table. The on-disk path is always derived from the
///     server-generated <c>documentId</c> plus extension, so no client-supplied string ever forms a filesystem path.
///     Chunk, section, and vector persistence are handled by separate lanes.
/// </summary>
public interface IKnowledgeDocumentBlobStore
{
    /// <summary>
    ///     Persists one document: writes the <c>knowledge_documents</c> row (status <c>Pending</c>) with an encrypted
    ///     display name and encrypts its raw bytes. Uploads dedupe by collection + content hash. Repository sources use
    ///     collection + source kind + source path identity, updating and invalidating projections when bytes change.
    /// </summary>
    Task<KnowledgeDocumentAddResult> AddAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken);

    /// <summary>Decrypts and returns the raw bytes for one document, or null when the row or blob is absent.</summary>
    Task<byte[]?> ReadBytesAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    ///     Removes only the on-disk encrypted bytes for one document, deriving the path from the server-generated
    ///     <paramref name="documentId" /> plus its <paramref name="extension" /> (never a stored path string). Used by the
    ///     purge service, which removes the metadata rows itself in an ordered transaction and then calls this so the file
    ///     deletion still runs after the row (and its extension) is gone. Best-effort: a missing file is a no-op.
    /// </summary>
    Task DeleteBytesAsync(Guid documentId, string extension, CancellationToken cancellationToken);

    /// <summary>
    ///     Lists the document ids that currently have on-disk bytes. Used by <c>KnowledgeBlobOrphanSweeper</c> to find
    ///     blobs whose <c>knowledge_documents</c> row is gone (a purge whose post-commit file delete failed) and reclaim
    ///     them. Any file whose leading name is not a document id is skipped, so a foreign file is never a candidate.
    /// </summary>
    IReadOnlyList<Guid> ListStoredDocumentIds();

    /// <summary>
    ///     Removes every on-disk file this store holds for one document. Unlike
    ///     <see cref="DeleteBytesAsync(Guid, string, CancellationToken)" /> it needs no extension, because the sweeper
    ///     reaches a document only once its row — and with it the stored extension — is already gone. Best-effort.
    /// </summary>
    Task DeleteAllBytesAsync(Guid documentId, CancellationToken cancellationToken);
}
