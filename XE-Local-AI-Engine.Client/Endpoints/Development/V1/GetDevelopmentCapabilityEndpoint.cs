namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

// Constructor injection is safe only because the IDevelopmentEndpoint marker keeps these endpoints out of FastEndpoints discovery (EndpointDiscoveryOptions.Filter) when Development:Enabled is false:
// discovery activates every endpoint at startup, AddNodeDevelopment only when the feature is on. GetDevelopmentCapabilityEndpoint must stay reachable with it off, so it carries no marker.

/// <summary>
///     Reports Development Mode's availability, and the state of the runtime it will actually execute on.
/// </summary>
/// <remarks>
///     The one endpoint in this file without the <c>IDevelopmentEndpoint</c> marker, so it stays registered with
///     Development Mode switched off; <see cref="IDevelopmentCapabilityService" /> is registered either way.
/// </remarks>
public sealed class GetDevelopmentCapabilityEndpoint : EndpointWithoutRequest<DevelopmentCapabilityResponse>
{
    private readonly IDevelopmentCapabilityService _capabilityService;

    public GetDevelopmentCapabilityEndpoint(IDevelopmentCapabilityService capabilityService)
    {
        ArgumentNullException.ThrowIfNull(capabilityService);
        _capabilityService = capabilityService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Capability);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var capability = await _capabilityService.GetAsync(ct);
        await Send.OkAsync(capability.ToResponse(), ct);
    }
}
