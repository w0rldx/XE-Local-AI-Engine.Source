namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Decides whether a just-stored knowledge document has to be queued for background ingestion, and queues it. Owns
///     the re-upload retry rule so the upload endpoint stays a bind → store → admit → map handler. SCOPED: it reads the
///     document status through the request-scoped catalog service; the dispatcher it enqueues onto is the process-wide
///     singleton queue.
/// </summary>
public interface IKnowledgeIngestionAdmissionService
{
    /// <summary>
    ///     Resolves the document's current status and enqueues ingestion when the document was freshly inserted or is a
    ///     dedupe hit in a retryable state. Returns that status plus whether the bounded queue rejected the admission.
    /// </summary>
    Task<KnowledgeIngestionAdmissionResult> AdmitUploadedDocumentAsync(Guid documentId, bool wasInserted, CancellationToken cancellationToken);
}

/// <summary>
///     Outcome of an upload admission: the document's resolved status (the value the upload response reports) and
///     whether the bounded ingestion queue was full, which the caller surfaces as a retryable busy response.
/// </summary>
public sealed record KnowledgeIngestionAdmissionResult(KnowledgeDocumentStatus Status, bool QueueFull);
