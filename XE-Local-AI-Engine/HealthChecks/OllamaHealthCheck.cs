namespace XE_Local_AI_Engine.HealthChecks
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.AI;
    using Microsoft.Extensions.Diagnostics.HealthChecks;
    using OllamaSharp;

    public sealed class OllamaHealthCheck : IHealthCheck
    {
        private readonly OllamaApiClient _ollamaClient;

        public OllamaHealthCheck(IChatClient chatClient)
        {
            ArgumentNullException.ThrowIfNull(chatClient);

            _ollamaClient = chatClient as OllamaApiClient
                ?? throw new InvalidOperationException("The registered IChatClient must be an OllamaApiClient for Ollama health checks.");
        }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isRunning = await _ollamaClient.IsRunningAsync(CancellationToken.None).ConfigureAwait(false);
            return isRunning
                ? HealthCheckResult.Healthy("Ollama is reachable.")
                : HealthCheckResult.Unhealthy("Ollama is unavailable.");
        }
    }
}
