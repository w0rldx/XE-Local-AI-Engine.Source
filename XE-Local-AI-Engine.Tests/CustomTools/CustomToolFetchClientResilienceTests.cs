namespace XE_Local_AI_Engine.Tests.CustomTools;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.CustomTools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The custom-tool fetch client carries no resilience pipeline: an action is sent exactly once (ADR 0021).
/// </summary>
/// <remarks>
///     Mirrors <c>LocalModelProxyResilienceTests</c>: the container must have the standard handler installed, as Aspire's
///     service defaults do, or the assertions pass whether or not <c>RemoveAllResilienceHandlers</c> was ever called.
///     Without that, one action attempt against a 500 reaches its target four times.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class CustomToolFetchClientResilienceTests
{
    private const string ClientName = HttpFetchExecutor.HttpClientName;

    // Mirrors PipelineNameHelper.GetName(ClientName, "standard"), the options name AddStandardResilienceHandler binds.
    private const string ResilienceOptionsName = ClientName + "-standard";

    [Test]
    public void FetchClient_UnderAspireDefaults_CarriesNoResilienceHandler()
    {
        using var provider = BuildProvider().Provider;

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);

        AssertEx.Empty(HandlerChain(handler).OfType<ResilienceHandler>().ToArray(),
            "The custom-tool fetch client must carry no resilience pipeline: a retried action repeats a side effect.");
    }

    [Test]
    public async Task FetchClient_WhenTheTargetAnswers500_SendsThePostExactlyOnce()
    {
        var built = BuildProvider();
        var provider = built.Provider;
        var upstream = built.Upstream;
        using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

            using var content = new StringContent("{}");
            using var response = await client.PostAsync(new Uri("http://203.0.113.10/api/services/light/fail"), content);

            AssertEx.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            AssertEx.Equal(expected: 1, upstream.Attempts, "An action POST must reach its target exactly once.");
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

    private static BuiltProvider BuildProvider()
    {
        var services = new ServiceCollection();
        var handler = new AttemptCountingHandler();

        // Mirror ServiceDefaults' Aspire branch: a global standard handler on every client, registered BEFORE the
        // production registration so the latter's RemoveAllResilienceHandlers strips it, exactly as in production.
        services.ConfigureHttpClientDefaults(static http => http.AddStandardResilienceHandler());

        services.AddCustomToolFetchClient();

        // Observe the real attempt count, and answer without a socket (the SSRF pin never dials).
        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        services.Configure<HttpStandardResilienceOptions>(ResilienceOptionsName, static options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });

        return new BuiltProvider(services.BuildServiceProvider(), handler);
    }

    /// <summary>The container and the handler that counts what reached the target.</summary>
    private sealed class BuiltProvider
    {
        public BuiltProvider(ServiceProvider provider, AttemptCountingHandler upstream)
        {
            Provider = provider;
            Upstream = upstream;
        }

        public ServiceProvider Provider { get; }

        public AttemptCountingHandler Upstream { get; }
    }

    /// <summary>Stands in for an action target that answers every attempt with a 500, and counts them.</summary>
    private sealed class AttemptCountingHandler : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref _attempts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
