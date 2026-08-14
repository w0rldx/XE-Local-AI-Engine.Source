namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler to retrieve the tracked status of a single GGUF download by model name
///     (GET model-fit/gguf/downloads/{modelName}). Returns 404 when the coordinator has no entry for that name —
///     either it was never started or the process restarted. Thin transport over
///     <see cref="IGgufDownloadCoordinator.GetStatus" />; no path, URL, or token is returned.
/// </summary>
public sealed class GetGgufDownloadStatusEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    : Endpoint<GetGgufDownloadStatusRequest, GgufDownloadStatusResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var status = _downloadCoordinator.GetStatus(modelName);
        if (status is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
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
            ct).ConfigureAwait(false);
    }
}
