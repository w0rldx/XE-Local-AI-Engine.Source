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
///     generating job returns 204, an unknown or already-terminal job 404.
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
        Description(x => x.Accepts<ImageJobRouteRequest>());
    }

    public override async Task HandleAsync(ImageJobRouteRequest req, CancellationToken ct)
    {
        var cancelled = await _coordinator.CancelAsync(req.JobId, ct);
        if (!cancelled)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
