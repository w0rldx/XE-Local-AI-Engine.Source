namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler to cancel an in-flight GGUF download (POST model-fit/download/cancel): thin transport over
///     <see cref="IGgufDownloadCoordinator" />, signalling the download's cancellation token by model name.
/// </summary>
/// <remarks>
///     Cancellation is cooperative (the GGUF store stops at the next byte/await boundary) and idempotent: a download
///     that already finished or was never started returns <c>cancelled:false</c>, not an error.
/// </remarks>
public sealed class CancelGgufDownloadEndpoint : Endpoint<CancelGgufDownloadRequest, CancelGgufDownloadResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator;

    public CancelGgufDownloadEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        _downloadCoordinator = downloadCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.DownloadCancel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancelGgufDownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var modelName = req.ModelName.Trim();
        var cancelled = _downloadCoordinator.Cancel(modelName);

        await Send.OkAsync(new CancelGgufDownloadResponse
            {
                ModelName = modelName,
                Cancelled = cancelled
            },
            ct);
    }
}
