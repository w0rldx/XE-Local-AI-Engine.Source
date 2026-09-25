namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Reports whether graph workflows are switched on for this node.
/// </summary>
/// <remarks>
///     The one route in the family carved out of the disabled-node 404 sweep in <c>Program</c>, exactly as
///     <c>development-workflows/capability</c> is: every other path under <c>graph-workflows/</c> answers a bodyless 404
///     with the feature off, which a client cannot tell from a broken route, so this is what lets the SPA hide the
///     feature and keep chat sending instead of reporting a load failure. Constructor injection is safe off:
///     <see cref="GraphWorkflowOptions" /> binds unconditionally and this family is never dropped from discovery.
/// </remarks>
public sealed class GetGraphWorkflowCapabilityEndpoint : EndpointWithoutRequest<GraphWorkflowCapabilityResponse>
{
    private readonly IOptions<GraphWorkflowOptions> _options;

    public GetGraphWorkflowCapabilityEndpoint(IOptions<GraphWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new GraphWorkflowCapabilityResponse
        {
            Enabled = _options.Value.Enabled
        }, ct);
}
