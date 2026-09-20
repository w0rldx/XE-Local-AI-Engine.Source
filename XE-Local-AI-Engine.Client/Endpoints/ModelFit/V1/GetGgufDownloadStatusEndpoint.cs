namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler retrieving one tracked GGUF download by model name (GET
///     model-fit/gguf/downloads/{modelName}), a thin transport over <see cref="IGgufDownloadCoordinator.GetStatus" />.
/// </summary>
/// <remarks>
///     A name the coordinator has no entry for answers 404 — it was never started, or the process restarted. No path,
///     URL or token is returned.
/// </remarks>
public sealed class GetGgufDownloadStatusEndpoint : Endpoint<GetGgufDownloadStatusRequest, GgufDownloadStatusResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator;

    public GetGgufDownloadStatusEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        _downloadCoordinator = downloadCoordinator;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.DownloadStatus);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetGgufDownloadStatusRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and probe
        // the decoded canonical name so model names with slashes (e.g. hf.co/org/repo:quant) resolve correctly.
        var modelName = ModelRouteName.Decode(req.ModelName)?.Trim();
        if (string.IsNullOrWhiteSpace(modelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var status = _downloadCoordinator.GetStatus(modelName);
        if (status is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(new GgufDownloadStatusResponse
            {
                OperationId = status.OperationId,
                OperationKind = status.OperationKind,
                ModelName = status.ModelName,
                Phase = status.Phase.ToString(),
                CompletedBytes = status.CompletedBytes,
                TotalBytes = status.TotalBytes,
                SanitizedError = status.SanitizedError,
                ErrorCode = status.ErrorCode,
                StartedAtUtc = status.StartedAtUtc,
                UpdatedAtUtc = status.UpdatedAtUtc
            },
            ct);
    }
}
