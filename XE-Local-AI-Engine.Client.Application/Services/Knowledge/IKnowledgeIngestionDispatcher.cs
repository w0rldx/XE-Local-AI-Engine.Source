namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The enqueue seam the upload endpoint calls after storing a document's blob: it writes the document id onto a
///     bounded background queue and returns immediately.
/// </summary>
/// <remarks>
///     The background worker drains the queue and runs the ingestion state machine with bounded concurrency. Singleton:
///     it owns the queue and no scoped state. The bound means a burst of uploads cannot grow the queue without limit —
///     an admission arriving while it is full is rejected with
///     <see cref="KnowledgeIngestionEnqueueResult.QueueFull" /> rather than silently dropped, so the caller surfaces a
///     retryable busy response instead of accreting unbounded pending work.
/// </remarks>
public interface IKnowledgeIngestionDispatcher
{
    /// <summary>
    ///     Attempts to queue one document for background ingestion, never blocking to wait for space.
    /// </summary>
    /// <remarks>
    ///     Returns <see cref="KnowledgeIngestionEnqueueResult.Accepted" /> when the id was admitted; an id already
    ///     queued or in flight is coalesced into one deferred follow-up run. Returns
    ///     <see cref="KnowledgeIngestionEnqueueResult.QueueFull" /> when the bounded queue is at capacity, and the
    ///     caller then reports a retryable busy condition.
    /// </remarks>
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
