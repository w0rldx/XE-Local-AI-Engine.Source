namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     FastEndpoints handler that lists every persisted image job newest-first (GET images/jobs). Thin transport over the
///     <see cref="IImageJobCoordinator" />. Operator-gated.
/// </summary>
public sealed class ListImageJobsEndpoint : EndpointWithoutRequest<ListImageJobsResponse>
{
    private readonly IImageJobCoordinator _coordinator;

    public ListImageJobsEndpoint(IImageJobCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Jobs);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var jobs = await _coordinator.ListAsync(ct);

        await Send.OkAsync(new ListImageJobsResponse
            {
                Items = [.. jobs.Select(static j => j.ToResponse())]
            },
            ct);
    }
}
