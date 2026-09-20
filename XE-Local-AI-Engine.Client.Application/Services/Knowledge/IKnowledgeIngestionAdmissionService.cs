namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Decides whether a just-stored knowledge document has to be queued for background ingestion, and queues it.
/// </summary>
/// <remarks>
///     Owns the (re-)enqueue rule for every store path — the upload endpoint and the repository importer — so neither
///     has to restate it. SCOPED: it reads the document status through the request-scoped catalog service; the
///     dispatcher it enqueues onto is the process-wide singleton queue.
/// </remarks>
public interface IKnowledgeIngestionAdmissionService
{
    /// <summary>
    ///     Resolves the document's current status, enqueues ingestion when required, and returns that status plus the
    ///     dispatcher's answer.
    /// </summary>
    /// <remarks>
    ///     Ingestion is enqueued when the store wrote the document — a fresh insert, or a repository document whose
    ///     bytes changed — or when it is a dedupe hit in a retryable state. The dispatcher's answer is
    ///     <see langword="null" /> when nothing was enqueued.
    /// </remarks>
    /// <param name="wasWritten">
    ///     Whether the store actually wrote this document (<c>WasInserted || WasUpdated</c>): written documents are
    ///     always queued, dedupe hits only from a retryable status.
    /// </param>
    Task<KnowledgeIngestionAdmissionResult> AdmitStoredDocumentAsync(Guid documentId, bool wasWritten, CancellationToken cancellationToken);
}

/// <summary>
///     Outcome of an admission: the document's resolved status — the value the upload response reports — and the
///     dispatcher's answer.
/// </summary>
/// <remarks>
///     The dispatcher's answer is <see langword="null" /> when the rule decided not to enqueue at all. The importer
///     counts an <see cref="KnowledgeIngestionEnqueueResult.Accepted" /> answer; both callers treat
///     <see cref="QueueFull" /> as a retryable busy condition.
/// </remarks>
public sealed class KnowledgeIngestionAdmissionResult
{
    public required KnowledgeDocumentStatus Status { get; init; }

    public required KnowledgeIngestionEnqueueResult? Enqueue { get; init; }

    /// <summary>Whether the bounded ingestion queue rejected this admission.</summary>
    public bool QueueFull => Enqueue is KnowledgeIngestionEnqueueResult.QueueFull;
}
