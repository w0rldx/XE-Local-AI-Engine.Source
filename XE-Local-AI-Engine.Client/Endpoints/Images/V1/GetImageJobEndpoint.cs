namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler that reads one image job's current status view (GET images/jobs/{jobId}); 404 when the job
///     is unknown. Thin transport over the <see cref="IImageJobCoordinator" />. Operator-gated.
/// </summary>
public sealed class GetImageJobEndpoint(IImageJobCoordinator coordinator)
    : Endpoint<ImageJobRouteRequest, ImageJobResponse>
{
    private readonly IImageJobCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.JobById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ImageJobRouteRequest req, CancellationToken ct)
    {
        var view = await _coordinator.GetAsync(req.JobId, ct).ConfigureAwait(false);
        if (view is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(view.ToResponse(), ct).ConfigureAwait(false);
    }
}
