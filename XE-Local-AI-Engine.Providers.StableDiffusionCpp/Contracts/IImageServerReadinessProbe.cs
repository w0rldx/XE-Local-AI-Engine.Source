namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Probes a just-spawned (or running) <c>sd-server</c> endpoint for readiness. Separated from the supervisor so
///     unit tests can drive readiness deterministically without a real HTTP server. sd-server exposes NO <c>/health</c>
///     route and binds its socket ONLY after the synchronous model load completes, so the production implementation
///     polls <c>GET /sdcpp/v1/capabilities</c> in a connect-refused retry loop — the first successful response is ready
///     (frozen spike §4A).
/// </summary>
internal interface IImageServerReadinessProbe
{
    /// <summary>
    ///     Waits until the server at <paramref name="baseAddress" /> answers <c>/sdcpp/v1/capabilities</c>, or the
    ///     deadline / cancellation elapses. Returns <see langword="true" /> when the server became ready within the window.
    /// </summary>
    /// <param name="baseAddress">The loopback server-root base URL.</param>
    /// <param name="readinessTimeout">Max time to wait for first readiness (cold-start budget).</param>
    /// <param name="ct">Cancellation for the wait.</param>
    Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct);

    /// <summary>
    ///     Performs a single, fast liveness check against <paramref name="baseAddress" /> (no polling) for the reuse-path
    ///     wedged-daemon guard. Returns <see langword="true" /> when the daemon answered <c>/sdcpp/v1/capabilities</c>.
    /// </summary>
    Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct);
}
