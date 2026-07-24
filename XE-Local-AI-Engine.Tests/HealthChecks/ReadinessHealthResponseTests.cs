namespace XE_Local_AI_Engine.Tests.HealthChecks;

using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The /health/ready payload must distinguish a Degraded worker (which still returns HTTP 200 because it
///     is serving local inference) with its per-check status, description, and structured reason data — so "degraded" is
///     never an indistinguishable 200. A Healthy check with no data carries no reason block.
/// </summary>
public sealed class ReadinessHealthResponseTests
{
    [Test]
    public void BuildPayload_DegradedCheck_CarriesStatusDescriptionAndReason()
    {
        var entry = new HealthReportEntry(HealthStatus.Degraded,
            "Worker token has expired and re-pairing is required.",
            TimeSpan.FromMilliseconds(5),
            exception: null,
            data: new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["tokenExpired"] = true
            });
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
        {
            ["worker"] = entry
        }, TimeSpan.FromMilliseconds(5));

        var json = JsonSerializer.Serialize(ReadinessHealthResponse.BuildPayload(report));

        AssertEx.True(json.Contains("\"status\":\"Degraded\"", StringComparison.Ordinal), $"The overall status must surface Degraded. Payload: {json}");
        AssertEx.True(json.Contains("re-pairing is required", StringComparison.Ordinal), $"The degraded check's description must be included. Payload: {json}");
        AssertEx.True(json.Contains("tokenExpired", StringComparison.Ordinal), $"The degraded check's reason data must be included. Payload: {json}");
    }

    [Test]
    public void BuildPayload_HealthyCheckWithNoData_OmitsReasonBlock()
    {
        var entry = new HealthReportEntry(HealthStatus.Healthy,
            "Worker is operating locally.",
            TimeSpan.FromMilliseconds(1),
            exception: null,
            data: null);
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>(StringComparer.Ordinal)
        {
            ["worker"] = entry
        }, TimeSpan.FromMilliseconds(1));

        var json = JsonSerializer.Serialize(ReadinessHealthResponse.BuildPayload(report));

        AssertEx.True(json.Contains("\"status\":\"Healthy\"", StringComparison.Ordinal), $"The overall status must surface Healthy. Payload: {json}");
        AssertEx.True(json.Contains("\"reason\":null", StringComparison.Ordinal), $"A check with no data must carry a null reason. Payload: {json}");
    }
}
