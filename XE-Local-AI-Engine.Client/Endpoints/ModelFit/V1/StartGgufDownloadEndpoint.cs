namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler to begin a GGUF file download (POST model-fit/download). Thin transport over the
///     <see cref="IGgufDownloadCoordinator" /> (which delegates to the staged Hugging Face acquisition transaction): it starts a
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

        GgufDownloadTicket ticket;
        try
        {
            ticket = await _downloadCoordinator.StartAsync(request, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (string.Equals(exception.Message, "ModelConflict", StringComparison.Ordinal))
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The model name or destination is already in use.")).ConfigureAwait(false);
            return;
        }
        catch (HuggingFaceDownloadException exception) when (exception.Reason is HuggingFaceDownloadFailure.DestinationConflict
                                                               or HuggingFaceDownloadFailure.HashMismatch)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The repository did not provide exact metadata compatible with this acquisition.")).ConfigureAwait(false);
            return;
        }
        await Send.OkAsync(new StartGgufDownloadResponse
            {
                ModelName = ticket.ModelName,
                AlreadyInFlight = ticket.AlreadyInFlight,
                OperationId = ticket.OperationId,
                OperationKind = ticket.OperationKind
            },
            ct).ConfigureAwait(false);
    }
}
