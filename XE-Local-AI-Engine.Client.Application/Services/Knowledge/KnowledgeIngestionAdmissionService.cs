namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IKnowledgeIngestionAdmissionService" />. Holds the upload admission rule that used to live in
///     the upload endpoint handler.
/// </summary>
public sealed class KnowledgeIngestionAdmissionService(
    IKnowledgeDocumentCatalogService catalogService,
    IKnowledgeIngestionDispatcher ingestionDispatcher) : IKnowledgeIngestionAdmissionService
{
    private readonly IKnowledgeDocumentCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher = ingestionDispatcher ?? throw new ArgumentNullException(nameof(ingestionDispatcher));

    public async Task<KnowledgeIngestionAdmissionResult> AdmitUploadedDocumentAsync(Guid documentId,
        bool wasInserted,
        CancellationToken cancellationToken)
    {
        // Resolve the current status once. Ingestion flips a document out of Pending the instant it starts, so a Pending
        // row is one that has NOT been ingested — either freshly inserted or a prior upload whose admission a full queue
        // rejected (503), leaving the persisted blob stranded. Enqueue when the document was freshly inserted OR is a
        // dedupe hit in a RETRYABLE state, so retrying a stranded or failed upload actually recovers instead of returning
        // success for work that was never queued. A dedupe hit already Indexed (or mid-ingestion) is left alone.
        // Admission is idempotent, so retrying a document already queued is a harmless no-op rather than a duplicate
        // ingestion.
        var status = await _catalogService.GetStatusAsync(documentId, cancellationToken).ConfigureAwait(false)
                     ?? KnowledgeDocumentStatus.Pending;

        if (!wasInserted && !IsRetryableOnReUpload(status))
        {
            return new KnowledgeIngestionAdmissionResult(status, QueueFull: false);
        }

        var admission = await _ingestionDispatcher.EnqueueAsync(documentId, cancellationToken).ConfigureAwait(false);
        return new KnowledgeIngestionAdmissionResult(status, admission == KnowledgeIngestionEnqueueResult.QueueFull);
    }

    /// <summary>
    ///     Whether re-uploading identical content whose document is already in <paramref name="status" /> should
    ///     re-enqueue ingestion.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Content-hash dedupe means a re-upload never inserts a second row, so re-enqueueing is the ONLY way a
    ///         re-upload can retry. <see cref="KnowledgeDocumentStatus.Failed" /> belongs here because the app's own
    ///         failure messages instruct the user to "retry" — and before this it did nothing at all: a failed document
    ///         was neither freshly inserted nor Pending, so the identical file came back deduped, unqueued, and reported
    ///         as success, leaving the original Failed row untouched with its original timestamp. The per-row reindex
    ///         action was the only working retry path, and no message ever mentioned it.
    ///     </para>
    ///     <para>
    ///         <see cref="KnowledgeDocumentStatus.Indexed" /> is excluded so a re-upload of already-indexed content is a
    ///         cheap no-op rather than a redundant re-index. The in-flight states (Extracting/Chunking/Embedding) are
    ///         excluded because that work is already running; admission is idempotent, but re-enqueueing them would
    ///         misreport an in-progress document as newly queued.
    ///     </para>
    /// </remarks>
    private static bool IsRetryableOnReUpload(KnowledgeDocumentStatus status)
    {
        return status is KnowledgeDocumentStatus.Pending or KnowledgeDocumentStatus.Failed;
    }
}
