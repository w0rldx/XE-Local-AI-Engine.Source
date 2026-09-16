namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

// Deliberately calls NEITHER Policies() NOR AllowAnonymous(): its only source of protection is the global
// Configurator in Program.cs. EndpointAuthorizationPolicyTests asserts this endpoint specifically resolves to
// the Operator policy — if the Configurator is ever deleted, this is the one endpoint that would actually go
// anonymous, and it is the one that test names. Every other endpoint carries its own redundant Policies(Operator)
// call, so none of them would notice.
//
// Excluded from the OpenAPI description (Options(x => x.ExcludeFromDescription()), FastEndpoints' passthrough to
// the native RouteHandlerBuilder extension): an internal diagnostics probe with no request/response contract has
// no business appearing in the hey-api-generated React client. `pnpm run openapi:check` is what proves the
// exclusion actually held.
public sealed class ConfiguratorCanaryProbeEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get(LocalApiRoutes.ApiFoundation.ConfiguratorCanaryProbe);
        Options(x => x.ExcludeFromDescription());
    }

    public override Task HandleAsync(CancellationToken ct) => Send.NoContentAsync(ct);
}
