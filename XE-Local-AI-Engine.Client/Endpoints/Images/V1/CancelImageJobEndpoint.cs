namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler that requests cancellation of an image job (POST images/jobs/{jobId}/cancel). The coordinator
///     picks clean-cancel (queued) vs kill+restart (generating) internally; a queued/generating job returns 204, an
///     unknown or already-terminal job returns 404. Operator-gated.
/// </summary>
public sealed class CancelImageJobEndpoint(IImageJobCoordinator coordinator)
    : Endpoint<ImageJobRouteRequest>
{
    private readonly IImageJobCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

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
        var cancelled = await _coordinator.CancelAsync(req.JobId, ct).ConfigureAwait(false);
        if (!cancelled)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
