namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Probes a just-spawned (or running) <c>whisper-server</c> endpoint for readiness. Separated from the supervisor
///     so unit tests can drive readiness deterministically without a real HTTP server.
/// </summary>
/// <remarks>
///     Readiness is a port accept followed by a health request, and <b>never</b> a line on the child's stdout: the
///     server's own "listening" line is fully buffered when stdout is not a TTY and has been observed absent while the
///     port was already live. The health route answers 200 once the model is loaded and 503 while it is loading —
///     including for the duration of an in-place model switch, which is why a 503 keeps the loop polling rather than
///     reporting failure.
/// </remarks>
internal interface IWhisperServerReadinessProbe
{
    /// <summary>
    ///     Waits until the server at <paramref name="baseAddress" /> reports healthy, or the deadline or cancellation
    ///     elapses. Returns <see langword="true" /> when the server became ready within the window.
    /// </summary>
    /// <param name="baseAddress">The loopback server-root base URL.</param>
    /// <param name="readinessTimeout">Max time to wait for first readiness (the cold-start budget).</param>
    /// <param name="ct">Cancellation for the wait.</param>
    Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct);

    /// <summary>
    ///     Performs a single, fast liveness check (no polling) for the reuse-path wedged-daemon guard. Returns
    ///     <see langword="true" /> when the daemon answered healthy.
    /// </summary>
    Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct);
}
