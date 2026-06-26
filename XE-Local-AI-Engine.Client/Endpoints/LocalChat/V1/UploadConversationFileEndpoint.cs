namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using SecurityOptions = XE_Local_AI_Engine.Client.Configuration.SecurityOptions;

/// <summary>
///     FastEndpoints handler for uploading one file attachment to a conversation (POST multipart). Enforces the
///     size cap + extension allowlist, sanitizes the client file name to a leaf (so no client string forms a path),
///     runs pure-.NET text extraction, and persists the encrypted bytes + cached Markdown via the upload store. The
///     storage path is server-generated; the original name is kept only as encrypted display metadata.
/// </summary>
public sealed class UploadConversationFileEndpoint(
    IConversationUploadedFileStore fileStore,
    IDocumentTextExtractor extractor,
    IOptions<SecurityOptions> securityOptions)
    : Endpoint<UploadConversationFileRequest, ConversationUploadedFileResponse>
{
    private const string DefaultMimeType = "application/octet-stream";

    private readonly IConversationUploadedFileStore _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
    private readonly IDocumentTextExtractor _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
    private readonly long _maxUploadBytes = (securityOptions ?? throw new ArgumentNullException(nameof(securityOptions))).Value.MaxUploadFileSizeMb * 1024L * 1024L;

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalChat.ConversationUploads);
        AllowFileUploads();
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UploadConversationFileRequest req, CancellationToken ct)
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

        DocumentExtractionResult extraction;
        using (var extractionStream = new MemoryStream(bytes, writable: false))
        {
            extraction = await _extractor.ExtractAsync(extractionStream, originalName, extension, ct).ConfigureAwait(false);
        }

        var input = new ConversationUploadedFileInput(
            req.ConversationId,
            Guid.NewGuid(),
            originalName,
            string.IsNullOrWhiteSpace(file.ContentType) ? DefaultMimeType : file.ContentType,
            extension,
            bytes.Length,
            bytes,
            extraction.Status,
            extraction.Markdown,
            extraction.ExtractedChars);

        var info = await _fileStore.AddAsync(input, ct).ConfigureAwait(false);
        await Send.OkAsync(info.ToResponse(), ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFormFile file, CancellationToken ct)
    {
        await using var upload = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
