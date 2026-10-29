namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Requests cancellation of an image job. Operator-gated.
/// </summary>
/// <remarks>
///     The coordinator picks clean-cancel (queued) against kill-and-restart (generating) internally; a queued or
///     generating job returns 204, an unknown job 404 and an already-terminal one 409.
/// </remarks>
public sealed class CancelImageJobEndpoint : Endpoint<ImageJobRouteRequest>
{
    private readonly IImageJobCoordinator _coordinator;

    public CancelImageJobEndpoint(IImageJobCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.JobCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST (job id from the route, no body): override the default application/json-only Accepts so a
        // body-less request is not rejected with 415 (see TriggerScheduledJobEndpoint for the full rationale).
        Description(x => x.Accepts<ImageJobRouteRequest>()
                          .Produces(StatusCodes.Status204NoContent)
                          .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ImageJobRouteRequest req, CancellationToken ct)
    {
        var cancelled = await _coordinator.CancelAsync(req.JobId, ct);
        if (!cancelled)
        {
            await Send.ConflictProblemAsync("The job already finished, so there is nothing to cancel.");
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
