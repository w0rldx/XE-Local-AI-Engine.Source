namespace XE_Local_AI_Engine.Client.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Represents worker health check.
/// </summary>
public sealed class WorkerHealthCheck : IHealthCheck
{
    private readonly IOllamaApiClient _ollamaClient;
    private readonly ITokenStore _tokenStore;
    private readonly IWorkerHubConnection _workerHubConnection;

    public WorkerHealthCheck(ITokenStore tokenStore,
        IWorkerHubConnection workerHubConnection,
        IOllamaApiClient ollamaClient)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _workerHubConnection = workerHubConnection ?? throw new ArgumentNullException(nameof(workerHubConnection));
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!await _ollamaClient.IsRunningAsync(CancellationToken.None).ConfigureAwait(false))
        {
            return HealthCheckResult.Unhealthy("Ollama is unavailable.");
        }

        var data = new Dictionary<string, object>
        {
            ["paired"] = _tokenStore.IsPaired,
            ["tokenExpired"] = _tokenStore.IsTokenExpired,
            ["connectionState"] = _workerHubConnection.State.ToString()
        };

        if (!_tokenStore.IsPaired)
        {
            return HealthCheckResult.Degraded("Worker is not paired with the Central Platform.", data: data);
        }

        if (_tokenStore.IsTokenExpired)
        {
            return HealthCheckResult.Degraded("Worker token has expired and re-pairing is required.", data: data);
        }

        if (_workerHubConnection.State == WorkerConnectionState.Error)
        {
            return HealthCheckResult.Degraded("Worker hub connection is in an error state.", data: data);
        }

        return HealthCheckResult.Healthy("Worker is healthy.", data);
    }
}
