namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The enqueue seam the upload endpoint calls after storing a document's blob. It writes the document id
///     onto a bounded background queue and returns immediately; the background worker drains the queue and runs the
///     ingestion state machine with bounded concurrency. Singleton — it owns the queue and no scoped state. Because the
///     queue is bounded, a burst of uploads cannot grow it without limit: an admission that arrives while the queue is
///     full is rejected (<see cref="KnowledgeIngestionEnqueueResult.QueueFull" />) rather than silently dropped, so the
///     caller can surface a retryable busy response instead of accreting unbounded pending work.
/// </summary>
public interface IKnowledgeIngestionDispatcher
{
    /// <summary>
    ///     Attempts to queue one document for background ingestion. Returns <see cref="KnowledgeIngestionEnqueueResult.Accepted" />
    ///     when the id was admitted. If that id is already queued or in flight, coalesces the request into one deferred
    ///     follow-up run. Returns <see cref="KnowledgeIngestionEnqueueResult.QueueFull" /> when the bounded queue is at
    ///     capacity (the caller then reports a retryable busy condition). Never blocks waiting for space.
    /// </summary>
    ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>Outcome of an <see cref="IKnowledgeIngestionDispatcher.EnqueueAsync" /> admission attempt.</summary>
public enum KnowledgeIngestionEnqueueResult
{
    /// <summary>The document id was admitted now or coalesced into a deferred follow-up and will be ingested by the background worker.</summary>
    Accepted,

    /// <summary>The bounded queue was full; the document was not admitted and the caller should signal a retryable busy state.</summary>
    QueueFull
}
