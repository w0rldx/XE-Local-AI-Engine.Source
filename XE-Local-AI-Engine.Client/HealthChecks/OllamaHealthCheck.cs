namespace XE_Local_AI_Engine.Client.HealthChecks;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using OllamaSharp;

/// <summary>
///     Represents ollama health check.
/// </summary>
public sealed class OllamaHealthCheck(IOllamaApiClient ollamaClient) : IHealthCheck
{
    private readonly IOllamaApiClient _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var isRunning = await _ollamaClient.IsRunningAsync(CancellationToken.None).ConfigureAwait(false);
        return isRunning
            ? HealthCheckResult.Healthy("Ollama is reachable.")
            : HealthCheckResult.Unhealthy("Ollama is unavailable.");
    }
}
