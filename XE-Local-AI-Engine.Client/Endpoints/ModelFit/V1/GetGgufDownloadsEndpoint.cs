namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler listing every tracked GGUF download status (GET model-fit/gguf/downloads): the current
///     snapshot for each entry in the coordinator's status registry, in-flight and recently-finished alike.
/// </summary>
/// <remarks>
///     The frontend polls this to rediscover downloads after navigation and to render a progress list. No path, URL or
///     token is returned; all fields are sanitized by <see cref="IGgufDownloadCoordinator" />.
/// </remarks>
public sealed class GetGgufDownloadsEndpoint : EndpointWithoutRequest<ListGgufDownloadsResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator;

    public GetGgufDownloadsEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        _downloadCoordinator = downloadCoordinator;
    }

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
                        OperationId = s.OperationId,
                        OperationKind = s.OperationKind,
                        ModelName = s.ModelName,
                        Phase = s.Phase.ToString(),
                        CompletedBytes = s.CompletedBytes,
                        TotalBytes = s.TotalBytes,
                        SanitizedError = s.SanitizedError,
                        ErrorCode = s.ErrorCode,
                        StartedAtUtc = s.StartedAtUtc,
                        UpdatedAtUtc = s.UpdatedAtUtc
                    })
                    .ToList();

        await Send.OkAsync(new ListGgufDownloadsResponse
        {
            Items = items
        }, ct);
    }
}
