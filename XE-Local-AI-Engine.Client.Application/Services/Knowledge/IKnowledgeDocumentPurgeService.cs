namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deletes one knowledge-base document and every row that depends on it. The runtime SQLite connection runs with
///     foreign-key enforcement OFF, so the schema's <c>ON DELETE CASCADE</c> never fires — this service issues the
///     explicit ordered deletes (vectors → chunks → sections → document row) inside a single transaction and then removes
///     the on-disk encrypted bytes. Scoped: it drives the request-scoped <c>NodeChatDbContext</c> connection.
/// </summary>
public interface IKnowledgeDocumentPurgeService
{
    /// <summary>
    ///     Purges the document with the given id and its dependent rows plus the encrypted blob. Returns
    ///     <see langword="false" /> when no document row existed (the endpoint maps that to a 404).
    /// </summary>
    Task<bool> PurgeAsync(Guid documentId, CancellationToken cancellationToken);
}
