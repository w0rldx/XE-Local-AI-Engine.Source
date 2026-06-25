namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Probes a just-spawned (or running) <c>llama-server</c> endpoint for readiness. Separated from the supervisor so
///     unit tests can drive readiness deterministically without a real HTTP server; the production implementation
///     (<see cref="LlamaServerHealthProbe" />) polls the server's <c>/health</c> endpoint until it reports ready.
/// </summary>
internal interface ILlamaServerHealthProbe
{
    /// <summary>
    ///     Waits until the server at <paramref name="baseAddress" /> is ready to serve, or the deadline / cancellation
    ///     elapses. Returns <see langword="true" /> when the server became responsive within the window.
    /// </summary>
    /// <param name="baseAddress">The localhost OpenAI-compatible base URL (ends with <c>/v1</c>).</param>
    /// <param name="readinessTimeout">Max time to wait for first readiness (cold-start budget).</param>
    /// <param name="ct">Cancellation for the wait.</param>
    Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct);

    /// <summary>
    ///     Performs a single, fast liveness check against <paramref name="baseAddress" /> (no polling) for the health
    ///     aggregation surface. Returns <see langword="true" /> when the server answered.
    /// </summary>
    Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct);
}
