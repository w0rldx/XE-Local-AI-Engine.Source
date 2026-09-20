namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler to begin a GGUF file download (POST model-fit/download), a thin transport over
///     <see cref="IGgufDownloadCoordinator" />, which delegates to the staged Hugging Face acquisition transaction.
/// </summary>
/// <remarks>
///     It starts a background, cancellable download keyed by the canonical model name and returns immediately with that
///     identity; the download runs detached, and the coordinator tracks progress and cancellation. No path or token is
///     accepted or returned.
/// </remarks>
public sealed class StartGgufDownloadEndpoint : Endpoint<StartGgufDownloadRequest, StartGgufDownloadResponse>
{
    private readonly IGgufDownloadCoordinator _downloadCoordinator;

    public StartGgufDownloadEndpoint(IGgufDownloadCoordinator downloadCoordinator)
    {
        ArgumentNullException.ThrowIfNull(downloadCoordinator);
        _downloadCoordinator = downloadCoordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.Download);
        Policies(NodeAuthorizationPolicies.Operator);
        // GgufDownloadExceptionHandler maps the synchronous acquisition/HF failures to these ProblemDetails statuses.
        Description(builder => builder.ProducesProblem(StatusCodes.Status403Forbidden)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
                                      .ProducesProblem(StatusCodes.Status507InsufficientStorage));
    }

    public override async Task HandleAsync(StartGgufDownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.RepoId))
        {
            AddError("A repository id is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var request = new GgufModelRequest
        {
            RepoId = req.RepoId.Trim(),
            FileName = string.IsNullOrWhiteSpace(req.FileName) ? null : req.FileName.Trim(),
            Quant = string.IsNullOrWhiteSpace(req.Quant) ? null : req.Quant.Trim(),
            Revision = string.IsNullOrWhiteSpace(req.Revision) ? null : req.Revision.Trim(),
            IncludeProjector = req.IncludeProjector
        };

        var ticket = await _downloadCoordinator.StartAsync(request, ct);

        await Send.OkAsync(new StartGgufDownloadResponse
            {
                ModelName = ticket.ModelName,
                AlreadyInFlight = ticket.AlreadyInFlight,
                OperationId = ticket.OperationId,
                OperationKind = ticket.OperationKind
            },
            ct);
    }
}
