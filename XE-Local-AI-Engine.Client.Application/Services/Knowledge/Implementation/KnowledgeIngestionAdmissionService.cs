namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

/// <summary>
///     Decides whether a just-stored knowledge document has to be queued for background ingestion, and queues it.
/// </summary>
/// <remarks>
///     Owns the (re-)enqueue rule for every store path — the upload endpoint and the repository importer's per-file loop —
///     so neither has to restate it. SCOPED: it reads the document status through the request-scoped catalog service; the
///     dispatcher it enqueues onto is the process-wide singleton queue.
/// </remarks>
public sealed class KnowledgeIngestionAdmissionService
{
    private readonly IKnowledgeDocumentCatalogService _catalogService;
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher;
    private readonly IKnowledgeIndexingNotifier _notifier;

    public KnowledgeIngestionAdmissionService(IKnowledgeDocumentCatalogService catalogService,
        IKnowledgeIngestionDispatcher ingestionDispatcher,
        IKnowledgeIndexingNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(ingestionDispatcher);
        ArgumentNullException.ThrowIfNull(notifier);
        _catalogService = catalogService;
        _ingestionDispatcher = ingestionDispatcher;
        _notifier = notifier;
    }

    /// <summary>
    ///     Resolves the document's current status, enqueues ingestion when required, and returns that status plus the
    ///     dispatcher's answer.
    /// </summary>
    /// <remarks>
    ///     Ingestion is enqueued when the store wrote the document and the row is still Pending or in flight, or when it
    ///     is a dedupe hit in a retryable state. No admission announces Pending: the read may be stale by publication, so
    ///     each sends a status-free refresh. A row already gone or (when written) already terminal is not enqueued; the
    ///     answer is then <see langword="null" />.
    /// </remarks>
    /// <param name="wasWritten">
    ///     Whether the store actually wrote this document (<c>WasInserted || WasUpdated</c>) rather than deduplicated
    ///     onto it.
    /// </param>
    public async Task<KnowledgeIngestionAdmissionResult> AdmitStoredDocumentAsync(Guid documentId,
        bool wasWritten,
        CancellationToken cancellationToken)
    {
        // Resolve the status once: a Pending row has NOT been ingested, so enqueue when the store WROTE the document or
        // it is a dedupe hit in a RETRYABLE state — a stranded or failed upload then recovers instead of faking success.
        if (await _catalogService.GetStatusAsync(documentId, cancellationToken) is not { } status)
        {
            // Purged after the store wrote or deduplicated onto it: nothing to ingest, so a list still showing it refetches.
            await _notifier.NotifyDocumentsChangedAsync(cancellationToken);
            return Result(KnowledgeDocumentStatus.Pending, enqueue: null);
        }

        // Any read below may be stale (a run, sweep or purge moved the row, even one this caller just wrote), which is why
        // the dispatcher only sends a status-free refresh: a Pending push could land after the newer truth.
        if (!wasWritten)
        {
            return IsRetryableOnReUpload(status)
                ? Result(status, await _ingestionDispatcher.EnqueueAsync(documentId, cancellationToken))
                : Result(status, enqueue: null);
        }

        if (status is KnowledgeDocumentStatus.Indexed or KnowledgeDocumentStatus.Failed)
        {
            // A drain sweep already ran the written revision to its end: a second run would only repeat that outcome.
            await _notifier.NotifyDocumentsChangedAsync(cancellationToken);
            return Result(status, enqueue: null);
        }

        // Pending, or in flight (a sweep's run, or a replaced revision's run still going): admit so it coalesces one follow-up.
        return Result(status, await _ingestionDispatcher.EnqueueAsync(documentId, cancellationToken));
    }

    private static KnowledgeIngestionAdmissionResult Result(KnowledgeDocumentStatus status, KnowledgeIngestionEnqueueResult? enqueue)
    {
        return new KnowledgeIngestionAdmissionResult
        {
            Status = status,
            Enqueue = enqueue
        };
    }

    /// <summary>
    ///     Whether re-uploading identical content whose document is already in <paramref name="status" /> should
    ///     re-enqueue ingestion.
    /// </summary>
    /// <remarks>
    ///     Content-hash dedupe means a re-upload never inserts a second row, so re-enqueueing is the ONLY way a re-upload
    ///     can retry. <see cref="KnowledgeDocumentStatus.Failed" /> belongs here because the app's own failure messages
    ///     instruct the user to retry; without it the identical file comes back deduped, unqueued and reported as success.
    ///     <see cref="KnowledgeDocumentStatus.Indexed" /> is excluded so a re-upload of indexed content is a cheap no-op,
    ///     and the in-flight states because re-enqueueing them would misreport running work as newly queued.
    /// </remarks>
    private static bool IsRetryableOnReUpload(KnowledgeDocumentStatus status)
    {
        return status is KnowledgeDocumentStatus.Pending or KnowledgeDocumentStatus.Failed;
    }
}
