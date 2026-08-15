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
///     embedding all run later in the background ingestion worker — this handler only stores, then hands the admission
///     decision (whether a fresh insert or a retryable dedupe hit has to be queued) to
///     <see cref="IKnowledgeIngestionAdmissionService" />.
/// </summary>
public sealed class UploadKnowledgeDocumentEndpoint(
    IKnowledgeDocumentBlobStore blobStore,
    IKnowledgeIngestionAdmissionService ingestionAdmission,
    IDocumentTextExtractor extractor,
    IOptions<KnowledgeBaseOptions> knowledgeBaseOptions,
    IOptions<SecurityOptions> securityOptions)
    : Endpoint<UploadKnowledgeDocumentRequest, UploadKnowledgeDocumentResponse>
{
    private const string DefaultMimeType = "application/octet-stream";

    private readonly IKnowledgeDocumentBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));

    private readonly IKnowledgeIngestionAdmissionService _ingestionAdmission =
        ingestionAdmission ?? throw new ArgumentNullException(nameof(ingestionAdmission));

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
        if (!KnowledgeCollectionScope.TryNormalize(req.CollectionId, out var collectionId))
        {
            AddError("The collection id may contain only letters, digits, '.', '_' or '-' and must be 128 characters or fewer.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var input = new KnowledgeDocumentInput(Guid.NewGuid(),
            originalName,
            string.IsNullOrWhiteSpace(file.ContentType) ? DefaultMimeType : file.ContentType,
            extension,
            bytes.Length,
            contentHash,
            bytes,
            _embeddingModel,
            collectionId);

        var result = await _blobStore.AddAsync(input, ct).ConfigureAwait(false);

        var admission = await _ingestionAdmission.AdmitStoredDocumentAsync(result.DocumentId, result.WasInserted, ct)
                                                 .ConfigureAwait(false);
        if (admission.QueueFull)
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

        await Send.OkAsync(new UploadKnowledgeDocumentResponse
            {
                DocumentId = result.DocumentId,
                Status = admission.Status,
                Deduplicated = !result.WasInserted
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var upload = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
