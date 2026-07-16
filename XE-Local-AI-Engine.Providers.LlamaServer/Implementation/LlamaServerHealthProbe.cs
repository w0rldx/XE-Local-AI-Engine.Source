namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Production <see cref="ILlamaServerHealthProbe" />: polls a llama-server <c>/health</c> endpoint over HTTP. A
///     <c>200</c> means ready; connection-refused while the process is still warming is normal and retried until the
///     readiness deadline.
/// </summary>
internal sealed class LlamaServerHealthProbe(HttpClient httpClient) : ILlamaServerHealthProbe
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    // AUD4-15: a hard per-attempt bound so ONE probe can never stall the poll loop for the whole readiness budget when
    // the server accepts the socket but never answers. Combined with a dedicated resilience-free HttpClient (see the DI
    // registration), the supervisor's poll cadence — not a hung/retried request — controls readiness-detection timing.
    private static readonly TimeSpan PerAttemptTimeout = TimeSpan.FromSeconds(1);

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var healthUri = HealthUri(baseAddress);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(readinessTimeout);

        try
        {
            while (!deadlineCts.IsCancellationRequested)
            {
                if (await TryProbeAsync(healthUri, deadlineCts.Token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(PollInterval, deadlineCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Readiness deadline elapsed without the server coming up.
            return false;
        }

        return false;
    }

    /// <inheritdoc />
    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        return TryProbeAsync(HealthUri(baseAddress), ct);
    }

    private async Task<bool> TryProbeAsync(Uri healthUri, CancellationToken ct)
    {
        // Bound THIS single attempt independently of the caller's (readiness-budget or liveness) token so a wedged
        // server that never answers is treated as "not ready yet" and the loop keeps its cadence, rather than blocking
        // for the whole budget on one request. One request per attempt — no retries.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(PerAttemptTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(healthUri, attemptCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Caller cancellation / readiness deadline — propagate.
        }
        catch (OperationCanceledException)
        {
            return false; // This attempt exceeded its own per-attempt bound — not up yet; keep polling.
        }
        catch (HttpRequestException)
        {
            return false; // Not up yet (or a transient connection failure) — caller decides whether to keep polling.
        }
    }

    // The endpoint base ends with /v1; /health is a sibling at the server root.
    private static Uri HealthUri(Uri baseAddress)
    {
        return new Uri(baseAddress, "/health");
    }
}
