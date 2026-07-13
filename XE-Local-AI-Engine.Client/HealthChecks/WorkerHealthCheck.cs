namespace XE_Local_AI_Engine.Client.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Represents worker health check.
/// </summary>
public sealed class WorkerHealthCheck : IHealthCheck
{
    private readonly ITokenStore _tokenStore;
    private readonly IWorkerHubConnection _workerHubConnection;

    public WorkerHealthCheck(ITokenStore tokenStore,
        IWorkerHubConnection workerHubConnection)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _workerHubConnection = workerHubConnection ?? throw new ArgumentNullException(nameof(workerHubConnection));
    }

    // Pairing with the Central Platform is OPTIONAL: a node runs standalone (local inference) with no pairing, so an
    // unpaired node must be reported Healthy — it must never fail /health/ready. Only a node that IS paired but whose
    // pairing is failing (expired token / errored hub connection) degrades. The local model runtime (llama.cpp /
    // llama-server) is an on-demand host process, not a persistent daemon, so it does not gate readiness — and Ollama is
    // an opt-in external provider that must never block readiness either. CheckHealthAsync stays async because
    // IHealthCheck mandates the Task-returning signature.
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["paired"] = _tokenStore.IsPaired,
            ["tokenExpired"] = _tokenStore.IsTokenExpired,
            ["connectionState"] = _workerHubConnection.State.ToString()
        };

        if (!_tokenStore.IsPaired)
        {
            // Local-only operation — pairing is not configured, so the node is fully ready.
            return Task.FromResult(HealthCheckResult.Healthy("Worker is operating locally (not paired with the Central Platform).", data));
        }

        if (_tokenStore.IsTokenExpired)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Worker token has expired and re-pairing is required.", data: data));
        }

        if (_workerHubConnection.State == WorkerConnectionState.Error)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Worker hub connection is in an error state.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("Worker is healthy.", data));
    }
}
