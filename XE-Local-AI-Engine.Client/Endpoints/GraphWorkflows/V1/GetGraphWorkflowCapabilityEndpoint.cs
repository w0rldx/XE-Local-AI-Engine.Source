namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Reports whether graph workflows are switched on for this node.
/// </summary>
/// <remarks>
///     The one route in the family carved out of the disabled-feature 404 sweep in <c>FeatureSwitchMiddleware</c>, exactly as
///     <c>development-workflows/capability</c> is: every other path under <c>graph-workflows/</c> answers a bodyless 404
///     with the feature off, which a client cannot tell from a broken route, so this is what lets the SPA hide the
///     feature and keep chat sending instead of reporting a load failure. The switch is read from node settings
///     per request, so a saved change shows without a restart, and this family is never dropped from discovery.
/// </remarks>
public sealed class GetGraphWorkflowCapabilityEndpoint : EndpointWithoutRequest<GraphWorkflowCapabilityResponse>
{
    private readonly INodeRuntimeSettings _runtimeSettings;

    public GetGraphWorkflowCapabilityEndpoint(INodeRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.OkAsync(new GraphWorkflowCapabilityResponse
        {
            Enabled = await _runtimeSettings.GetGraphWorkflowsEnabledAsync(ct)
        }, ct);
}
