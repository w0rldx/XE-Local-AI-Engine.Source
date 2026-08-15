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
///     size cap + extension allowlist and sanitizes the client file name to a leaf (so no client string forms a path),
///     then hands the file to <see cref="IConversationUploadIngestor" />, which runs the gated extract-and-persist
///     phase. The storage path is server-generated; the original name is kept only as encrypted display metadata.
/// </summary>
public sealed class UploadConversationFileEndpoint(IConversationUploadIngestor ingestor, IOptions<SecurityOptions> securityOptions)
    : Endpoint<UploadConversationFileRequest, ConversationUploadedFileResponse>
{
    private readonly IConversationUploadIngestor _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
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
        if (!_ingestor.IsSupportedExtension(extension))
        {
            AddError($"Files of type '{extension}' are not supported.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        await using var content = file.OpenReadStream();
        var info = await _ingestor.IngestAsync(req.ConversationId, content, originalName, extension, file.ContentType, ct).ConfigureAwait(false);
        if (info is null)
        {
            // The extraction admission gate is full — fail fast with a busy status + Retry-After rather than letting
            // concurrent in-flight byte[] copies aggregate to an out-of-memory condition.
            HttpContext.Response.Headers.RetryAfter = "5";
            await Send.StringAsync("The server is busy processing uploads. Please retry shortly.",
                StatusCodes.Status503ServiceUnavailable,
                cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(info.ToResponse(), ct).ConfigureAwait(false);
    }
}
