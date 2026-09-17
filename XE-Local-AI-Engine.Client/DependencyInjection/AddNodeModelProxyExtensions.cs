namespace XE_Local_AI_Engine.Client.DependencyInjection;

using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Registers the inbound OpenAI-compatible model proxy and the dedicated HTTP client it forwards through. Its own
///     extension rather than two inline lines in the composition root because the client's deadline contract is
///     load-bearing and testable: <c>LocalModelProxyResilienceTests</c> calls this method against a container that has
///     the Aspire defaults installed, which is the only honest way to prove the contract still holds.
/// </summary>
internal static class AddNodeModelProxyExtensions
{
    public static IServiceCollection AddNodeModelProxy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: the forwarder resolves the per-request GGUF catalog and streams one response.
        services.AddScoped<LocalModelProxyForwarder>();

        // The forwarding client carries an INFINITE timeout and NO resilience pipeline, and both halves matter.
        //
        // A long generation must not be severed by a client-side timeout — the caller's disconnect (request-abort) and
        // the forwarder's own inter-read idle watchdog are the cancellation signals instead. But an infinite
        // HttpClient.Timeout alone does not deliver that under Aspire: AddServiceDefaults installs a standard
        // resilience handler on EVERY named client through ConfigureHttpClientDefaults, including one registered
        // later, and that pipeline's per-attempt and total-request timeouts sever the request long before the model is
        // done. With HttpCompletionOption.ResponseHeadersRead a NON-streaming completion sends no response headers
        // until generation ends, so the whole generation sits inside one attempt and trips the attempt timeout; the
        // TimeoutRejectedException that follows is neither HttpRequestException nor IOException, so it escapes the
        // forwarder's pre-header 503 arm and reaches the caller as an unhandled 500.
        //
        // The pipeline also retries every method by default, and an inference POST is not idempotent — a retried
        // completion bills the operator's GPU twice for one request, and a dead child gets retried instead of
        // answering the retryable 503 the forwarder already writes. RemoveAllResilienceHandlers strips both halves,
        // and is a no-op outside Aspire.
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
