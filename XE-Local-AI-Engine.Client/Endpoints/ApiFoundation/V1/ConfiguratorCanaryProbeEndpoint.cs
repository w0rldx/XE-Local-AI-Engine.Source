namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

// Deliberately calls NEITHER Policies() NOR AllowAnonymous(): the global Configurator in Program.cs is its only protection, so deleting the Configurator makes this the one
// endpoint that goes anonymous — the break EndpointAuthorizationPolicyTests names. Also excluded from the OpenAPI description; docs/wiki/09-api-and-hubs.md ("Dev-only surfaces").
public sealed class ConfiguratorCanaryProbeEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get(LocalApiRoutes.ApiFoundation.ConfiguratorCanaryProbe);
        Options(x => x.ExcludeFromDescription());
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.NoContentAsync(ct);
}
