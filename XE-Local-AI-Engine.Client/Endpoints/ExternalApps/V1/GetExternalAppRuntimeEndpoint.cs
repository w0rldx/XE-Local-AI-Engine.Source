namespace XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ExternalApps.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     The resolved container runtime for the Runtime panel. A pure read: it never pins a daemon and never reconciles,
///     which is why <c>foreignInstallContainers</c> is 0 here — the count is the reconciler's observation, and
///     reporting a cached one as a fresh one would tell the operator a foreign container is there when it is not.
/// </summary>
public sealed class GetExternalAppRuntimeEndpoint : EndpointWithoutRequest<ExternalAppRuntimeResponse>
{
    private readonly IContainerRuntimeResolver _resolver;

    public GetExternalAppRuntimeEndpoint(IContainerRuntimeResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        _resolver = resolver;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ExternalApps.Runtime);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolution = await _resolver.ResolveAsync(cancellationToken: ct);
        await Send.OkAsync(ExternalAppMapper.ToRuntimeResponse(resolution, foreignInstallContainers: 0), ct);
    }
}
