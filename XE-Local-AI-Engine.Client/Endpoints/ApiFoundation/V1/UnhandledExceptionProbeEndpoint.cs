namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

// Framework-probe endpoint mirroring ValidationProblemProbe: it deliberately throws an unhandled exception so the DefaultExceptionHandler to RFC7807 ProblemDetails
// pipeline can be asserted end-to-end, the W3C trace id on ProblemDetails.traceId included. Operator-authorized like the other diagnostics probes.
public sealed class UnhandledExceptionProbeEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post(LocalApiRoutes.ApiFoundation.UnhandledExceptionProbe);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        throw new InvalidOperationException("Diagnostics exception probe: forced unhandled exception.");
    }
}
