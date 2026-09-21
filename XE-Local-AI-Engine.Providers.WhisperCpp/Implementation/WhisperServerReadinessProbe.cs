namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Production <see cref="IWhisperServerReadinessProbe" />: polls the server's health route until it answers 2xx.
/// </summary>
/// <remarks>
///     Three states are distinguished and each is handled differently. A connection failure means the socket is not
///     bound yet and is retried. A 503 means the process is up and loading a model — including during an in-place
///     model switch — and is also retried, which is the whole reason a health route beats a transcription probe here.
///     A 2xx means ready. The child's stdout is never consulted: its "listening" line is fully buffered off a TTY.
/// </remarks>
internal sealed class WhisperServerReadinessProbe : IWhisperServerReadinessProbe
{
    /// <summary>The readiness route, relative to the server root.</summary>
    internal const string HealthRoute = "health";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient;
    private readonly WhisperRuntimeOptions _options;

    public WhisperServerReadinessProbe(HttpClient httpClient, WhisperRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    /// <inheritdoc />
    public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ct.ThrowIfCancellationRequested();

        var healthUri = new Uri(baseAddress, HealthRoute);
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
            // The readiness deadline elapsed without the server reporting healthy.
            return false;
        }

        // The loop can also end because the CALLER's token fired, and the two must not be confused: the supervisor turns a false into "the runtime did not become ready in time", so reporting a
        // shutdown that way would misclassify it as a product failure. Only the deadline returns false.
        ct.ThrowIfCancellationRequested();
        return false;
    }

    /// <inheritdoc />
    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        return TryProbeAsync(new Uri(baseAddress, HealthRoute), ct);
    }

    private async Task<bool> TryProbeAsync(Uri healthUri, CancellationToken ct)
    {
        // Each probe owns its own short deadline. The runtime HttpClient carries an infinite timeout on purpose (one client serves requests whose right budgets differ by four orders of magnitude), so
        // without this a daemon that accepts the socket and never answers would hang the poll rather than failing this attempt.
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(_options.HealthProbeTimeout);

        try
        {
            using var response = await _httpClient.GetAsync(healthUri, probeCts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // This probe exceeded its own budget, not the caller's: not ready, keep polling.
            return false;
        }
        catch (HttpRequestException)
        {
            // Socket not bound yet, or a transient connection failure: not ready, keep polling.
            return false;
        }
    }
}
