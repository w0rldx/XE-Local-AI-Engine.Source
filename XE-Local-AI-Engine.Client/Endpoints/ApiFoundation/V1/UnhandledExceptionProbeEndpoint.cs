namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

// Framework-probe endpoint (mirrors ValidationProblemProbe): deliberately throws an unhandled exception so the
// DefaultExceptionHandler -> RFC7807 ProblemDetails pipeline can be asserted end-to-end, including the W3C trace id
// carried in ProblemDetails.traceId. Operator-authorized like the other diagnostics probes.
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
