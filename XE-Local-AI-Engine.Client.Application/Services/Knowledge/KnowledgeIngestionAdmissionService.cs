namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IKnowledgeIngestionAdmissionService" />. Holds the single admission rule that used to live in
///     the upload endpoint handler and, duplicated, in the repository importer's per-file loop.
/// </summary>
public sealed class KnowledgeIngestionAdmissionService : IKnowledgeIngestionAdmissionService
{
    private readonly IKnowledgeDocumentCatalogService _catalogService;
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher;

    public KnowledgeIngestionAdmissionService(IKnowledgeDocumentCatalogService catalogService,
        IKnowledgeIngestionDispatcher ingestionDispatcher)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(ingestionDispatcher);
        _catalogService = catalogService;
        _ingestionDispatcher = ingestionDispatcher;
    }

    public async Task<KnowledgeIngestionAdmissionResult> AdmitStoredDocumentAsync(Guid documentId,
        bool wasWritten,
        CancellationToken cancellationToken)
    {
        // Resolve the status once: a Pending row has NOT been ingested, so enqueue when the store WROTE the document or
        // it is a dedupe hit in a RETRYABLE state — a stranded or failed upload then recovers instead of faking success.
        var status = await _catalogService.GetStatusAsync(documentId, cancellationToken)
                     ?? KnowledgeDocumentStatus.Pending;

        if (!wasWritten && !IsRetryableOnReUpload(status))
        {
            return new KnowledgeIngestionAdmissionResult
            {
                Status = status,
                Enqueue = null
            };
        }

        var admission = await _ingestionDispatcher.EnqueueAsync(documentId, cancellationToken);
        return new KnowledgeIngestionAdmissionResult
        {
            Status = status,
            Enqueue = admission
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
