namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Records the operator's explicit approval of the container runtime currently reachable.</summary>
/// <remarks>
///     Its own endpoint rather than a flag on the capability GET, because pinning a daemon is a decision and a GET
///     must not make decisions: a page refresh, a prefetch or a health check would otherwise silently approve
///     whatever daemon happened to be answering.
/// </remarks>
public sealed class ConfirmDevelopmentContainerRuntimeEndpoint : Endpoint<ConfirmDevelopmentContainerRuntimeRequest, DevelopmentContainerRuntimeResponse>, IDevelopmentEndpoint
{
    private readonly IDockerDaemonPreflightService _dockerDaemonPreflight;

    public ConfirmDevelopmentContainerRuntimeEndpoint(IDockerDaemonPreflightService dockerDaemonPreflight)
    {
        ArgumentNullException.ThrowIfNull(dockerDaemonPreflight);
        _dockerDaemonPreflight = dockerDaemonPreflight;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.ContainerRuntimeConfirmation);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ConfirmDevelopmentContainerRuntimeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DaemonId))
        {
            AddError("A container runtime id is required so the confirmation approves the runtime you were shown.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var preflight = await _dockerDaemonPreflight.ConfirmAsync(req.DaemonId, ct);

        await Send.OkAsync(preflight.ToResponse(), ct);
    }
}
