namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Decides whether a just-stored knowledge document has to be queued for background ingestion, and queues it. Owns
///     the (re-)enqueue rule for every store path — the upload endpoint and the repository importer — so neither has to
///     restate it. SCOPED: it reads the document status through the request-scoped catalog service; the dispatcher it
///     enqueues onto is the process-wide singleton queue.
/// </summary>
public interface IKnowledgeIngestionAdmissionService
{
    /// <summary>
    ///     Resolves the document's current status and enqueues ingestion when the store wrote the document (a fresh
    ///     insert, or a repository document whose bytes changed) or it is a dedupe hit in a retryable state. Returns that
    ///     status plus the dispatcher's answer — <see langword="null" /> when nothing was enqueued.
    /// </summary>
    /// <param name="documentId">The stored document's id.</param>
    /// <param name="wasWritten">
    ///     Whether the store actually wrote this document (<c>WasInserted || WasUpdated</c>). Written documents are always
    ///     queued; unwritten ones (dedupe hits) are queued only from a retryable status.
    /// </param>
    /// <param name="cancellationToken">Cancels the status lookup and the enqueue.</param>
    Task<KnowledgeIngestionAdmissionResult> AdmitStoredDocumentAsync(Guid documentId, bool wasWritten, CancellationToken cancellationToken);
}

/// <summary>
///     Outcome of an admission: the document's resolved status (the value the upload response reports) and the
///     dispatcher's answer, which is <see langword="null" /> when the rule decided not to enqueue at all. The importer
///     counts an <see cref="KnowledgeIngestionEnqueueResult.Accepted" /> answer; both callers treat
///     <see cref="QueueFull" /> as a retryable busy condition.
/// </summary>
public sealed record KnowledgeIngestionAdmissionResult(KnowledgeDocumentStatus Status, KnowledgeIngestionEnqueueResult? Enqueue)
{
    /// <summary>Whether the bounded ingestion queue rejected this admission.</summary>
    public bool QueueFull => Enqueue is KnowledgeIngestionEnqueueResult.QueueFull;
}
