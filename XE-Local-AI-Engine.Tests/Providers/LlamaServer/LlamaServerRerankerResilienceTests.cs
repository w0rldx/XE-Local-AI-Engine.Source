namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The reranker client's deadline contract: <c>/v1/rerank</c> is a non-idempotent POST that scores a whole pool on
///     the operator's own GPU, so it gets its OWN named client with no resilience pipeline — while the idempotent
///     catalog/download GETs on the shared default client deliberately keep theirs.
/// </summary>
/// <remarks>
///     <para>
///         <b>The container under test must have the standard resilience handler installed, or the assertions here are
///         vacuous.</b> That is what Aspire's <c>AddServiceDefaults</c> does to every client through
///         <c>ConfigureHttpClientDefaults</c>; a test that omits it passes whether or not
///         <c>RemoveAllResilienceHandlers</c> was ever called. Mirrors <c>LocalModelProxyResilienceTests</c>, which
///         records the live round that lesson came from.
///     </para>
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class LlamaServerRerankerResilienceTests
{
    private const string ClientName = LlamaServerRerankerClient.HttpClientName;

    // Mirrors PipelineNameHelper.GetName(ClientName, "standard") — the options name AddStandardResilienceHandler binds.
    private const string ResilienceOptionsName = ClientName + "-standard";

    [Test]
    public void RerankClient_UnderAspireDefaults_CarriesNoResilienceHandler()
    {
        using var provider = BuildProvider().Provider;

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);

        AssertEx.Empty(HandlerChain(handler).OfType<ResilienceHandler>().ToArray(),
            "The rerank client must carry no resilience pipeline: its blanket retries re-score the whole pool on the "
            + "operator's GPU, and its attempt timeout severs a pool the client's own budget still allows.");
    }

    [Test]
    public async Task RerankClient_WhenTheServerFails_SendsTheNonIdempotentPostExactlyOnce()
    {
        var (provider, upstream) = BuildProvider();
        using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

            using var content = new StringContent("{}");
            using var response = await client.PostAsync(new Uri("http://127.0.0.1:18100/v1/rerank"), content);

            AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertEx.Equal(expected: 1, upstream.Attempts, "A rerank POST scores the pool; a retry bills the GPU twice for one search.");
        }
    }

    [Test]
    public void RerankClient_KeepsAFiniteTimeoutAboveItsOwnBudgetCeiling()
    {
        // The trap this test exists for: AddStandardResilienceHandler sets HttpClient.Timeout to
        // Timeout.InfiniteTimeSpan (its pipeline owns the deadline), and RemoveAllResilienceHandlers removes the
        // HANDLER, not that mutation. A registration that only strips therefore leaves the client under Aspire with no
        // pipeline AND no deadline at all.
        //
        // The one put back is finite, NOT infinite as on the proxy and the whisper runtime client: the per-call timeout
        // override the reranker's constructor accepts is unbounded, so "the caller always owns a deadline" is not a
        // property this registration can guarantee. Above the ceiling means the client's own linked-token budget is
        // still what fires in production, with a backstop underneath it that ends a hung request.
        using var provider = BuildProvider().Provider;

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        AssertEx.True(client.Timeout != Timeout.InfiniteTimeSpan, "An unbounded rerank would stall knowledge search forever.");
        AssertEx.True(client.Timeout > LlamaServerRerankerClient.ResolveRequestTimeout(int.MaxValue),
            $"The backstop ({client.Timeout}) must sit above the reranker's own budget ceiling, or it fires first and the "
            + "degrade-to-fusion-order path never runs.");
    }

    [Test]
    public void DefaultClient_KeepsItsPipeline_ForTheIdempotentCatalogAndDownloadGets()
    {
        // The deliberate KEEP. The release catalog and the binary manager still take the shared default client, where a
        // retried GET costs a second read and never a second side effect — this pins that the fix above narrowed the
        // strip to the reranker instead of disabling resilience for the whole provider stack.
        using var provider = BuildProvider().Provider;

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(Options.DefaultName);

        AssertEx.NotEmpty(HandlerChain(handler).OfType<ResilienceHandler>().ToArray(),
            "The default client serves idempotent catalog/download GETs and must keep the Aspire pipeline.");
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

        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient();
        services.AddLlamaServerLocalModelProvider();

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

    /// <summary>Stands in for a rerank-role llama-server that answers every attempt with a retryable failure, and counts them.</summary>
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
