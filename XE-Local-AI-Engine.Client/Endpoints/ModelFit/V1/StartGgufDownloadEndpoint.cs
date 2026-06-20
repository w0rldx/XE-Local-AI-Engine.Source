namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     FastEndpoints handler to begin a GGUF file download (POST model-fit/download). Thin transport over the
///     <see cref="IGgufDownloadCoordinator" /> (which delegates to the Hugging Face GGUF store, <see cref="IGgufModelStore" />): it starts a
///     background, cancellable download keyed by the canonical model name and returns immediately with that identity. The
///     download runs detached; progress/cancel are tracked by the coordinator. No path/token is accepted or returned.
/// </summary>
public sealed class StartGgufDownloadEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    : Endpoint<StartGgufDownloadRequest, StartGgufDownloadResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator = downloadCoordinator ?? throw new ArgumentNullException(nameof(downloadCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.Download);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(StartGgufDownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RepoId))
        {
            AddError("A repository id is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var request = new GgufModelRequest
        {
            RepoId = req.RepoId.Trim(),
            FileName = string.IsNullOrWhiteSpace(req.FileName) ? null : req.FileName.Trim(),
            Quant = string.IsNullOrWhiteSpace(req.Quant) ? null : req.Quant.Trim(),
            Revision = string.IsNullOrWhiteSpace(req.Revision) ? null : req.Revision.Trim()
        };

        var ticket = await _downloadCoordinator.StartAsync(request, ct).ConfigureAwait(false);
        await Send.OkAsync(new StartGgufDownloadResponse
            {
                ModelName = ticket.ModelName,
                AlreadyInFlight = ticket.AlreadyInFlight
            },
            ct).ConfigureAwait(false);
    }
}
