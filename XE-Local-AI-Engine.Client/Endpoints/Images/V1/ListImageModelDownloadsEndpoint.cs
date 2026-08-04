namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler listing every tracked image-model download (GET images/models/downloads) — in flight and
///     recently finished. This is how a failed weight download becomes visible: the operator UI polls it while a
///     download is pending and surfaces the <c>Failed</c> phase with its sanitized reason instead of waiting forever for
///     a model that will never appear. Mirrors <c>GET model-fit/gguf/downloads</c>. No path, URL, or token is returned.
/// </summary>
public sealed class ListImageModelDownloadsEndpoint(IImageModelDownloadCoordinator downloadCoordinator)
    : EndpointWithoutRequest<ListImageModelDownloadsResponse>
{
    private readonly IImageModelDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.ModelDownloads);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var items = _downloadCoordinator.ListStatuses()
                                        .Select(status => new ImageModelDownloadStatusResponse
                                        {
                                            ModelName = status.ModelName,
                                            Phase = status.Phase.ToString(),
                                            CompletedBytes = status.CompletedBytes,
                                            TotalBytes = status.TotalBytes,
                                            SanitizedError = status.SanitizedError,
                                            PartIndex = status.PartIndex,
                                            PartCount = status.PartCount
                                        })
                                        .ToList();

        await Send.OkAsync(new ListImageModelDownloadsResponse
        {
            Items = items
        }, ct).ConfigureAwait(false);
    }
}
