namespace XE_Local_AI_Engine.Tests.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using XE_Local_AI_Engine.Client.HealthChecks;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Readiness must not be polluted by the OPTIONAL Central Platform pairing: an unpaired (local-only) node is
///     Healthy, and only a node that IS paired but whose pairing is failing degrades.
/// </summary>
public sealed class WorkerHealthCheckTests
{
    [Test]
    public async Task Unpaired_LocalOnly_IsHealthy()
    {
        // The reported defect: an unpaired node returned Degraded on the "ready"-tagged check and failed /health/ready.
        var result = await Evaluate(paired: false, tokenExpired: false, WorkerConnectionState.Disconnected);

        AssertEx.Equal(HealthStatus.Healthy, result.Status);
        AssertEx.Equal(expected: false, result.Data["paired"]);
    }

    [Test]
    public async Task Paired_Connected_WithValidToken_IsHealthy()
    {
        var result = await Evaluate(paired: true, tokenExpired: false, WorkerConnectionState.Connected);

        AssertEx.Equal(HealthStatus.Healthy, result.Status);
    }

    [Test]
    public async Task Paired_WithExpiredToken_IsDegraded()
    {
        var result = await Evaluate(paired: true, tokenExpired: true, WorkerConnectionState.Connected);

        AssertEx.Equal(HealthStatus.Degraded, result.Status);
    }

    [Test]
    public async Task Paired_WithHubConnectionError_IsDegraded()
    {
        var result = await Evaluate(paired: true, tokenExpired: false, WorkerConnectionState.Error);

        AssertEx.Equal(HealthStatus.Degraded, result.Status);
    }

    private static async Task<HealthCheckResult> Evaluate(bool paired, bool tokenExpired, WorkerConnectionState state)
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.IsPaired.Returns(paired);
        tokenStore.IsTokenExpired.Returns(tokenExpired);

        var hub = Substitute.For<IWorkerHubConnection>();
        hub.State.Returns(state);

        var check = new WorkerHealthCheck(tokenStore, hub);
        return await check.CheckHealthAsync(new HealthCheckContext());
    }
}
