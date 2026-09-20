namespace XE_Local_AI_Engine.Client.DependencyInjection;

using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Registers the inbound OpenAI-compatible model proxy and the dedicated HTTP client it forwards through.
/// </summary>
/// <remarks>
///     Its own extension rather than two inline lines in the composition root because the client's deadline contract is
///     load-bearing and testable: <c>LocalModelProxyResilienceTests</c> calls this method against a container that has
///     the Aspire defaults installed, which is the only honest way to prove the contract still holds.
/// </remarks>
internal static class AddNodeModelProxyExtensions
{
    /// <summary>
    ///     Registers the forwarder and its named client, which carries an INFINITE timeout and NO resilience pipeline.
    /// </summary>
    /// <remarks>
    ///     A long generation must not be severed by a client-side timeout — the caller's disconnect and the inter-read idle watchdog are the cancellation
    ///     signals — but an infinite <c>HttpClient.Timeout</c> alone does not deliver that under Aspire, whose <c>AddServiceDefaults</c> installs
    ///     a standard resilience handler on EVERY named client, including one registered later. With <c>ResponseHeadersRead</c> a non-streaming completion
    ///     sends no headers until generation ends, so it sits inside one attempt and trips the attempt timeout, and the
    ///     <c>TimeoutRejectedException</c> escapes the pre-header 503 arm and reaches the caller as a 500.
    /// </remarks>
    public static IServiceCollection AddNodeModelProxy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: the forwarder resolves the per-request GGUF catalog and streams one response.
        services.AddScoped<LocalModelProxyForwarder>();

        // INFINITE timeout and NO resilience pipeline, both halves load-bearing: the Aspire pipeline also retries every method, and an inference POST is not
        // idempotent — a retry bills the GPU twice and retries a dead child instead of the retryable 503 the forwarder writes. RemoveAllResilienceHandlers is a no-op outside Aspire.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; used deliberately to drop the
        // Aspire-installed standard pipeline, whose attempt timeout and blanket retries
        // are both wrong for a reverse proxy in front of local inference.
        services.AddHttpClient(LocalModelProxyForwarder.HttpClientName)
                .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
                .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        return services;
    }
}
