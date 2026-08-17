namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetGgufDownloadOperationStatusEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    : Endpoint<GetGgufDownloadOperationStatusRequest, GgufDownloadStatusResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.DownloadOperationStatus);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetGgufDownloadOperationStatusRequest req, CancellationToken ct)
    {
        var status = _downloadCoordinator.GetStatus(req.OperationId);
        if (status is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(GgufDownloadStatusMapper.Map(status), ct).ConfigureAwait(false);
    }
}
