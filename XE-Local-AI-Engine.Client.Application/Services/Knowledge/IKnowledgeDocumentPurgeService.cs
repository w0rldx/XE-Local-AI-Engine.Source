namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deletes one knowledge-base document and every row that depends on it, then the on-disk encrypted bytes; scoped,
///     so it drives the request-scoped <c>NodeChatDbContext</c> connection.
/// </summary>
/// <remarks>
///     Vectors, chunks and sections declare a cascade the node connection does enforce, but the deletes stay explicit and ordered
///     (vectors → chunks → sections → document row) in one transaction. Not for the FTS index: SQLite fires
///     <c>knowledge_document_chunks_ad</c> for a cascade-removed chunk too (pinned by a test). Chunk → section is SET NULL, so
///     chunks precede sections or the rows get rewritten on their way out, and the method needs file locations and counts.
/// </remarks>
public interface IKnowledgeDocumentPurgeService
{
    /// <summary>
    ///     Purges the document with the given id and its dependent rows plus the encrypted blob. Returns
    ///     <see langword="false" /> when no document row existed (the endpoint maps that to a 404).
    /// </summary>
    Task<bool> PurgeAsync(Guid documentId, CancellationToken cancellationToken);
}
