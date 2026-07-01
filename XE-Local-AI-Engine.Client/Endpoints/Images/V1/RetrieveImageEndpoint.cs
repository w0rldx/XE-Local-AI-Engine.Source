namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>Route-only request for <c>GET images/{imageId}</c>.</summary>
public sealed class RetrieveImageRequest
{
    /// <summary>Route-bound, server-generated image id (never a client-supplied path).</summary>
    public Guid ImageId { get; init; }
}

/// <summary>
///     FastEndpoints handler that streams a generated image's decrypted bytes (GET images/{imageId}). The blob store
///     decrypts on read; 404 when the image id is unknown or its blob is missing. The response is served inline with the
///     stored MIME type (image/png) and marked no-store — the plaintext image only ever exists in transit. No prompt,
///     path, or filename is surfaced. Operator-gated.
/// </summary>
public sealed class RetrieveImageEndpoint(IGeneratedImageStore imageStore)
    : Endpoint<RetrieveImageRequest>
{
    private readonly IGeneratedImageStore _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.ImageById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(RetrieveImageRequest req, CancellationToken ct)
    {
        var content = await _imageStore.OpenReadAsync(req.ImageId, ct).ConfigureAwait(false);
        if (content is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        // Decrypted image bytes are private per-operator content — keep them out of any shared/proxy cache.
        HttpContext.Response.Headers.CacheControl = "no-store";

        // fileName is intentionally null so the response is served inline (no Content-Disposition attachment) and no
        // server-side name leaks.
        await Send.BytesAsync(content.Bytes.ToArray(), fileName: null, contentType: content.MimeType, cancellation: ct).ConfigureAwait(false);
    }
}
