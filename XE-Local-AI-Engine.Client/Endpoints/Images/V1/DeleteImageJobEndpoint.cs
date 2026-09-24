namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Deletes one image job with its generated images — rows and encrypted blobs. Operator-gated.
/// </summary>
/// <remarks>
///     204 when it is gone, 404 for an unknown job, and 409 while the job is still queued or generating: the node
///     refuses an active job rather than cancelling it on the operator's behalf, the same posture the benchmark
///     project delete takes.
/// </remarks>
public sealed class DeleteImageJobEndpoint : Endpoint<ImageJobRouteRequest>
{
    private readonly IImageJobCoordinator _coordinator;

    public DeleteImageJobEndpoint(IImageJobCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Images.JobById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ImageJobRouteRequest req, CancellationToken ct)
    {
        var outcome = await _coordinator.DeleteAsync(req.JobId, ct);

        switch (outcome)
        {
            case ImageJobDeleteOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                return;

            case ImageJobDeleteOutcome.NotTerminal:
                // The outcome stays machine-readable as an `outcome` extension member so the SPA can tell "still
                // running, cancel it first" from any other conflict without matching on the message.
                await Send.ConflictProblemAsync("The job is still queued or generating. Cancel it, then delete it.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["outcome"] = outcome.ToString()
                    });
                return;

            default:
                await Send.NoContentAsync(ct);
                return;
        }
    }
}
