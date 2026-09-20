namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deletes one knowledge-base document and every row that depends on it. Vectors, chunks and sections declare a
///     cascade the node connection does enforce, but the deletes stay explicit and ordered (vectors → chunks →
///     sections → document row) inside a single transaction. Two reasons the schema cannot cover: the chunk delete is
///     what fires <c>knowledge_document_chunks_ad</c>, the AFTER DELETE trigger that keeps the external-content
///     <c>chunk_fts</c> index aligned — whether SQLite fires a row trigger for a row removed by a foreign-key cascade
///     action is UNMEASURED here, and the failure mode is a silently stale search index rather than an error — and
///     chunks must precede sections, because chunk → section is SET NULL and would otherwise rewrite rows on their way
///     out. Then it removes the on-disk encrypted bytes. Scoped: it drives the request-scoped <c>NodeChatDbContext</c> connection.
/// </summary>
public interface IKnowledgeDocumentPurgeService
{
    /// <summary>
    ///     Purges the document with the given id and its dependent rows plus the encrypted blob. Returns
    ///     <see langword="false" /> when no document row existed (the endpoint maps that to a 404).
    /// </summary>
    Task<bool> PurgeAsync(Guid documentId, CancellationToken cancellationToken);
}
