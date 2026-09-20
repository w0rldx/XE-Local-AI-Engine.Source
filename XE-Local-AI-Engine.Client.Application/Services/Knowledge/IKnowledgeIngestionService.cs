namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Runs the per-document ingestion state machine (Pending → Extracting → Chunking → Embedding → Indexed, or → Failed).
/// </summary>
/// <remarks>
///     Scoped: it uses the scoped <see cref="XE_Local_AI_Engine.Client.Persistence.NodeChatDbContext" /> and is resolved
///     inside the per-document scope the background worker creates. The endpoint never calls it directly; it enqueues
///     via <see cref="IKnowledgeIngestionDispatcher" />.
/// </remarks>
public interface IKnowledgeIngestionService
{
    /// <summary>
    ///     Advances one document through extraction, chunking, embedding, and indexing. Never throws for an expected
    ///     failure: any step failure sets the document to <c>Failed</c> with a content-free reason.
    /// </summary>
    Task RunAsync(Guid documentId, CancellationToken cancellationToken);
}
