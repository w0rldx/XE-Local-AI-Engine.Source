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
    ///     display name, deduping on content hash, and encrypts the raw bytes to disk when the row is newly inserted.
    /// </summary>
    Task<KnowledgeDocumentAddResult> AddAsync(KnowledgeDocumentInput input, CancellationToken cancellationToken);

    /// <summary>Decrypts and returns the raw bytes for one document, or null when the row or blob is absent.</summary>
    Task<byte[]?> ReadBytesAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>Removes one document's metadata row plus its on-disk encrypted bytes. Returns whether a row existed.</summary>
    Task<bool> DeleteAsync(Guid documentId, CancellationToken cancellationToken);
}
