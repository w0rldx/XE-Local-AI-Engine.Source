namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>Maps the anonymous <c>/health/live</c> and <c>/health/ready</c> probes.</summary>
internal static class HealthEndpointExtensions
{
    internal static WebApplication MapNodeHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // AllowAnonymous is load-bearing, not decorative: under the FallbackPolicy an endpoint with no auth
        // metadata is challenged, and Aspire's WithHttpHealthCheck poll carries no token.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false // don't run any checks; just return 200 if the app can serve requests
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            // Explicit and intentional: Healthy and Degraded both return 200, because a Degraded worker is still serving local inference and readiness consumers such as
            // Aspire's WithHttpHealthCheck poll must keep it in rotation, while the payload distinguishes it per check. Only Unhealthy — a dead node-SQLite store — is 503.
            ResultStatusCodes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
            },
            ResponseWriter = ReadinessHealthResponse.WriteAsync
        }).AllowAnonymous();

        return app;
    }
}
