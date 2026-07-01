namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The enqueue seam the upload endpoint (Lane D) calls after storing a document's blob. It writes the document id
///     onto a background queue and returns immediately; the background worker drains the queue and runs the ingestion
///     state machine with bounded concurrency. Singleton — it owns the queue and no scoped state.
/// </summary>
public interface IKnowledgeIngestionDispatcher
{
    /// <summary>Queues one document for background ingestion. Returns once the id is accepted onto the queue.</summary>
    ValueTask EnqueueAsync(Guid documentId, CancellationToken cancellationToken);
}
