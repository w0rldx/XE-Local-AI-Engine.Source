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

        // Only enqueue a freshly inserted document; a dedupe hit already exists (and may already be indexed), so
        // re-running the pipeline would be wasted work.
        if (result.WasInserted)
        {
            await _ingestionDispatcher.EnqueueAsync(result.DocumentId, ct).ConfigureAwait(false);
        }

        var status = await _catalogService.GetStatusAsync(result.DocumentId, ct).ConfigureAwait(false)
                     ?? KnowledgeDocumentStatus.Pending;

        await Send.OkAsync(new UploadKnowledgeDocumentResponse
            {
                DocumentId = result.DocumentId,
                Status = status,
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
