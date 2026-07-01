namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Production <see cref="IImageServerReadinessProbe" />: polls <c>GET /sdcpp/v1/capabilities</c>. sd-server has NO
///     <c>/health</c> route and binds its socket only AFTER the synchronous model load completes, so the first response
///     that is not a connection failure means "ready" (frozen spike §4A). Connection-refused while the process is still
///     loading is normal and retried until the readiness deadline. Mirrors <c>LlamaServerHealthProbe</c>.
/// </summary>
internal sealed class ImageServerReadinessProbe(HttpClient httpClient) : IImageServerReadinessProbe
{
    /// <summary>The readiness route — sd-server exposes no <c>/health</c>; capabilities is the first route to answer once bound.</summary>
    internal const string CapabilitiesRoute = "sdcpp/v1/capabilities";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var capabilitiesUri = new Uri(baseAddress, CapabilitiesRoute);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(readinessTimeout);

        try
        {
            while (!deadlineCts.IsCancellationRequested)
            {
                if (await TryProbeAsync(capabilitiesUri, deadlineCts.Token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(PollInterval, deadlineCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Readiness deadline elapsed without the server binding its socket.
            return false;
        }

        return false;
    }

    private async Task<bool> TryProbeAsync(Uri capabilitiesUri, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(capabilitiesUri, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false; // Socket not bound yet (or a transient connection failure) — keep polling.
        }
    }
}
