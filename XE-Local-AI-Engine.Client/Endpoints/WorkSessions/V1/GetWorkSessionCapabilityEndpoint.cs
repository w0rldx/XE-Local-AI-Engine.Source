namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Reports whether work sessions are switched on for this node.
/// </summary>
/// <remarks>
///     The one route in the family carved out of the disabled-feature 404 sweep in <c>FeatureSwitchMiddleware</c>, exactly as <c>development/capability</c> is: every other path under
///     <c>work-sessions/</c> answers a bodyless 404 with the feature off, which a client cannot tell from a broken route, so this is what lets the SPA say
///     "switched off on this node" rather than "could not load work sessions". The literal <c>capability</c> segment outranks <c>{sessionId}</c> in route matching, and
///     the switch is read from node settings per request, so a saved change shows without a restart.
/// </remarks>
public sealed class GetWorkSessionCapabilityEndpoint : EndpointWithoutRequest<WorkSessionCapabilityResponse>
{
    private readonly INodeRuntimeSettings _runtimeSettings;

    public GetWorkSessionCapabilityEndpoint(INodeRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(new WorkSessionCapabilityResponse
        {
            Enabled = await _runtimeSettings.GetWorkSessionsEnabledAsync(ct)
        }, ct);
}
