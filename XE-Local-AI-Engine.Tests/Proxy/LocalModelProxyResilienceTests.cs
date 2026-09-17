namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The forwarding HTTP client's deadline contract: it reverse-proxies local inference, so it carries an infinite
///     client timeout and NO resilience pipeline at all.
/// </summary>
/// <remarks>
///     <para>
///         <b>The container under test must have the standard resilience handler installed, or every assertion here is
///         vacuous.</b> That is what Aspire's <c>AddServiceDefaults</c> does to every named client through
///         <c>ConfigureHttpClientDefaults</c>, and a test that omits it passes whether or not
///         <c>RemoveAllResilienceHandlers</c> was ever called — the exact shape of test that let the inherited
///         attempt timeout reach a live round, where a non-streaming completion answered 500
///         "The operation didn't complete within the allowed timeout" instead of streaming to the end.
///     </para>
///     <para>
///         The registration is reached through <c>AddNodeModelProxyExtensions.AddNodeModelProxy</c>, the same method
///         the composition root calls, rather than by booting a host: the test host does not set <c>ASPIRE_ENABLED</c>,
///         so a host-based assertion would be one of the vacuous ones described above.
///     </para>
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class LocalModelProxyResilienceTests
{
    private const string ClientName = LocalModelProxyForwarder.HttpClientName;

    // Mirrors PipelineNameHelper.GetName(ClientName, "standard") — the options name AddStandardResilienceHandler binds.
    private const string ResilienceOptionsName = ClientName + "-standard";

    [Test]
    public void ForwardingClient_UnderAspireDefaults_CarriesNoResilienceHandler()
    {
        // Structural rather than timed, because the half that cannot be proved cheaply by behaviour is the attempt
        // timeout: showing it does NOT fire means outlasting it, and a generation legitimately outlasts it by minutes.
        using var provider = BuildProvider().Provider;

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);

        var resilience = HandlerChain(handler).OfType<ResilienceHandler>().ToArray();
        AssertEx.Empty(resilience,
            "The forwarding client must carry no resilience pipeline: its attempt timeout severs a long non-streaming "
            + "generation, and the TimeoutRejectedException that follows is neither HttpRequestException nor "
            + "IOException, so it escapes the forwarder's 503 arm as an unhandled 500.");
    }

    [Test]
    public void ForwardingClient_UnderAspireDefaults_HasAnInfiniteTimeout()
    {
        // The other half, and what catches a dropped Timeout.InfiniteTimeSpan: the 100-second default would abort a
        // long generation on its own, resilience pipeline or not.
        using var provider = BuildProvider().Provider;

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        AssertEx.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Test]
    public async Task ForwardingClient_WhenTheChildFails_SendsTheNonIdempotentPostExactlyOnce()
    {
        // An inference POST is not idempotent. A retried completion bills the operator's GPU twice for one request,
        // and retrying a dead child delays the retryable 503 the forwarder already knows how to write.
        var (provider, upstream) = BuildProvider();
        using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

            using var content = new StringContent("{}");
            using var response = await client.PostAsync(new Uri("http://127.0.0.1:18100/v1/chat/completions"), content);

            AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertEx.Equal(expected: 1, upstream.Attempts, "A non-idempotent inference POST must reach the child exactly once.");
        }
    }

    private static IEnumerable<HttpMessageHandler> HandlerChain(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is not null)
        {
            yield return current;
            current = (current as DelegatingHandler)?.InnerHandler;
        }
    }

    private static (ServiceProvider Provider, AttemptCountingHandler Upstream) BuildProvider()
    {
        var services = new ServiceCollection();
        var handler = new AttemptCountingHandler();

        // Mirror ServiceDefaults' Aspire branch: a global standard handler on every client, registered BEFORE the
        // production registration so the latter's RemoveAllResilienceHandlers strips it, exactly as in production.
        services.ConfigureHttpClientDefaults(static http => http.AddStandardResilienceHandler());

        services.AddNodeModelProxy();

        // Observe the real attempt count, and answer without a socket.
        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        // Test-only tuning so a RETRIED attempt (the failure this guards against) adds no real delay to the run.
        // Leaves the retry count and predicate alone — collapsing the backoff must not hide the retry itself.
        services.Configure<HttpStandardResilienceOptions>(ResilienceOptionsName, static options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });

        return (services.BuildServiceProvider(), handler);
    }

    /// <summary>Stands in for a llama-server child that answers every attempt with a retryable failure, and counts them.</summary>
    private sealed class AttemptCountingHandler : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
