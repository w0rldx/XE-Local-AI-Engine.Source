namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using System.Security.Cryptography;
using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     FastEndpoints handler for uploading one document into the knowledge base (POST multipart). Enforces the size cap +
///     extension allowlist, sanitizes the client file name to a leaf (so no client string forms a path), computes the
///     content hash for dedupe, and persists the encrypted bytes via the blob store. Text extraction, chunking, and
///     embedding all run later in the background ingestion worker — this handler only stores + enqueues. Enqueue happens
///     only for a freshly inserted document, so a dedupe hit never re-runs the pipeline.
/// </summary>
public sealed class UploadKnowledgeDocumentEndpoint(
    IKnowledgeDocumentBlobStore blobStore,
    IKnowledgeIngestionDispatcher ingestionDispatcher,
    IKnowledgeDocumentCatalogService catalogService,
    IDocumentTextExtractor extractor,
    IOptions<KnowledgeBaseOptions> knowledgeBaseOptions,
    IOptions<SecurityOptions> securityOptions)
    : Endpoint<UploadKnowledgeDocumentRequest, UploadKnowledgeDocumentResponse>
{
    private const string DefaultMimeType = "application/octet-stream";

    private readonly IKnowledgeDocumentBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher = ingestionDispatcher ?? throw new ArgumentNullException(nameof(ingestionDispatcher));
    private readonly IKnowledgeDocumentCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly IDocumentTextExtractor _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    private readonly string _embeddingModel = (knowledgeBaseOptions ?? throw new ArgumentNullException(nameof(knowledgeBaseOptions))).Value.EmbeddingModelName;
    private readonly long _maxUploadBytes = (securityOptions ?? throw new ArgumentNullException(nameof(securityOptions))).Value.MaxUploadFileSizeMb * 1024L * 1024L;

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.Documents);
        AllowFileUploads();
        // Declare the multipart body so FastEndpoints documents it in OpenAPI (and the request is not rejected with a 415
        // for lacking a JSON body). Mirrors the typed-IFormFile + AllowFileUploads pattern of the conversation upload.
        Description(builder => builder.Accepts<UploadKnowledgeDocumentRequest>("multipart/form-data"));
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UploadKnowledgeDocumentRequest req, CancellationToken ct)
    {
        var file = req.File ?? (Files.Count > 0 ? Files[0] : null);
        if (file is null)
        {
            AddError("A file is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (file.Length == 0)
        {
            AddError("The file is empty.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (file.Length > _maxUploadBytes)
        {
            AddError($"The file exceeds the maximum upload size of {_maxUploadBytes / (1024L * 1024L)} MB.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var originalName = UploadFileNameSanitizer.ToSafeLeafFileName(file.FileName);
        if (originalName is null)
        {
            AddError("The file name is invalid.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var extension = Path.GetExtension(originalName);
        if (!_extractor.IsSupported(extension))
        {
            AddError($"Files of type '{extension}' are not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var bytes = await ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes));

        var input = new KnowledgeDocumentInput(Guid.NewGuid(),
            originalName,
            string.IsNullOrWhiteSpace(file.ContentType) ? DefaultMimeType : file.ContentType,
            extension,
            bytes.Length,
            contentHash,
            bytes,
            _embeddingModel);

        var result = await _blobStore.AddAsync(input, ct).ConfigureAwait(false);

        // Resolve the current status once. Ingestion flips a document out of Pending the instant it starts, so a Pending
        // row is one that has NOT been ingested — either freshly inserted or a prior upload whose admission a full queue
        // rejected (503), leaving the persisted blob stranded. Enqueue when the document was freshly inserted OR is a
        // dedupe hit in a RETRYABLE state, so retrying a stranded or failed upload actually recovers instead of returning
        // success for work that was never queued. A dedupe hit already Indexed (or mid-ingestion) is left alone.
        // Admission is idempotent, so retrying a document already queued is a harmless no-op rather than a duplicate
        // ingestion.
        var status = await _catalogService.GetStatusAsync(result.DocumentId, ct).ConfigureAwait(false)
                     ?? KnowledgeDocumentStatus.Pending;

        if (result.WasInserted || IsRetryableOnReUpload(status))
        {
            var admission = await _ingestionDispatcher.EnqueueAsync(result.DocumentId, ct).ConfigureAwait(false);
            if (admission == KnowledgeIngestionEnqueueResult.QueueFull)
            {
                // The bounded ingestion queue is full: the blob is persisted (so a retry dedupes to it) but background
                // indexing was not admitted. Fail with the same busy status + Retry-After the conversation upload uses so
                // the client retries shortly rather than the server growing an unbounded backlog. The worker's drain-sweep
                // (or a retry once the queue drains) picks the stranded document up.
                HttpContext.Response.Headers.RetryAfter = "5";
                await Send.StringAsync("The server is busy indexing documents. Please retry shortly.",
                    StatusCodes.Status503ServiceUnavailable,
                    cancellation: ct).ConfigureAwait(false);
                return;
            }
        }

        await Send.OkAsync(new UploadKnowledgeDocumentResponse
            {
                DocumentId = result.DocumentId,
                Status = status,
                Deduplicated = !result.WasInserted
            },
            ct).ConfigureAwait(false);
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

    private static async Task<byte[]> ReadAllBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var upload = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
