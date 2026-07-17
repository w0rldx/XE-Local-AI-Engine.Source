namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Text.Json;
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

    /// <inheritdoc />
    public async Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        // Bound this single read independently of the caller so a wedged /props never stalls the spawn path; one request.
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attemptCts.CancelAfter(PerAttemptTimeout);
        try
        {
            using var response = await _httpClient.GetAsync(PropsUri(baseAddress), attemptCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: attemptCts.Token).ConfigureAwait(false);

            // /props exposes the effective per-slot context under default_generation_settings.n_ctx.
            if (document.RootElement.TryGetProperty("default_generation_settings", out var settings)
                && settings.ValueKind == JsonValueKind.Object
                && settings.TryGetProperty("n_ctx", out var nCtx)
                && nCtx.ValueKind == JsonValueKind.Number
                && nCtx.TryGetInt32(out var contextTokens)
                && contextTokens > 0)
            {
                return contextTokens;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Caller cancellation — propagate.
        }
        catch (OperationCanceledException)
        {
            return null; // This read exceeded its per-attempt bound — treat the effective context as unknown.
        }
        catch (HttpRequestException)
        {
            return null; // /props unreachable — unknown.
        }
        catch (JsonException)
        {
            return null; // Malformed /props body — unknown.
        }
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

    // /props is a sibling of /health at the server root (not under /v1).
    private static Uri PropsUri(Uri baseAddress)
    {
        return new Uri(baseAddress, "/props");
    }
}
