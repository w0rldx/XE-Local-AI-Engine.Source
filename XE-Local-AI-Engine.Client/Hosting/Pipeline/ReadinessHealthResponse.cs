namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
///     Projects a readiness <see cref="HealthReport" /> into the <c>/health/ready</c> JSON payload.
/// </summary>
/// <remarks>
///     Each check reports its own status, description and structured reason data, so a Degraded worker that still
///     returns HTTP 200 — it is serving local inference — is nonetheless distinguishable by an inspecting operator
///     or dashboard.
/// </remarks>
public static class ReadinessHealthResponse
{
    public static object BuildPayload(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(static entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                reason = entry.Value.Data.Count == 0
                    ? null
                    : entry.Value.Data.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                duration = entry.Value.Duration.TotalMilliseconds
            }).ToArray()
        };
    }

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(BuildPayload(report), CancellationToken.None);
    }
}
