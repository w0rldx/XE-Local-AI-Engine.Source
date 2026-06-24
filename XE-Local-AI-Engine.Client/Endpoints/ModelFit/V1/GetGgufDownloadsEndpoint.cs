namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler to list all tracked GGUF download statuses (GET model-fit/gguf/downloads). Returns the
///     current snapshot for every entry in the coordinator's status registry — both in-flight and recently-finished
///     downloads. The FE polls this to rediscover downloads after navigation and to render a progress list.
///     No path, URL, or token is returned; all fields are sanitized by <see cref="IGgufDownloadCoordinator" />.
/// </summary>
public sealed class GetGgufDownloadsEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    : EndpointWithoutRequest<ListGgufDownloadsResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Downloads);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var statuses = _downloadCoordinator.ListStatuses();
        var items = statuses
            .Select(s => new GgufDownloadStatusResponse
            {
                ModelName = s.ModelName,
                Phase = s.Phase.ToString(),
                CompletedBytes = s.CompletedBytes,
                TotalBytes = s.TotalBytes,
                SanitizedError = s.SanitizedError
            })
            .ToList();

        await Send.OkAsync(new ListGgufDownloadsResponse { Items = items }, ct).ConfigureAwait(false);
    }
}
