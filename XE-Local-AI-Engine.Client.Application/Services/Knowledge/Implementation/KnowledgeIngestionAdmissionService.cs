namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

/// <summary>
///     Default <see cref="IKnowledgeIngestionAdmissionService" />. Holds the single admission rule the upload endpoint and the
///     repository importer's per-file loop both apply.
/// </summary>
public sealed class KnowledgeIngestionAdmissionService : IKnowledgeIngestionAdmissionService
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
