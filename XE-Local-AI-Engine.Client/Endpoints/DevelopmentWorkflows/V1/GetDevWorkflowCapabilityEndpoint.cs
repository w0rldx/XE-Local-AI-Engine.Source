namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Reports whether development workflows are switched on for this node.
/// </summary>
/// <remarks>
///     The one route in the family carved out of the disabled-node 404 sweep in <c>Program</c>, exactly as
///     <c>development/capability</c> is: every other path under <c>development-workflows/</c> answers a bodyless 404
///     with the feature off, which a client cannot tell from a broken route, so this is what lets the SPA say
///     "switched off on this node" rather than "could not load the work items". Constructor injection is safe off:
///     <see cref="DevWorkflowOptions" /> binds unconditionally and this family is never dropped from discovery.
/// </remarks>
public sealed class GetDevWorkflowCapabilityEndpoint : EndpointWithoutRequest<DevWorkflowCapabilityResponse>
{
    private readonly IOptions<DevWorkflowOptions> _options;

    public GetDevWorkflowCapabilityEndpoint(IOptions<DevWorkflowOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new DevWorkflowCapabilityResponse { Enabled = _options.Value.Enabled }, ct);
}
