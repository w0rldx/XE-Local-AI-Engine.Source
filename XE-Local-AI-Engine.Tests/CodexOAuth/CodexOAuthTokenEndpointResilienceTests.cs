namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Codex OAuth token endpoint's client must carry NO resilience pipeline: both requests it sends are
///     single-use grants. The authorization code is consumed by the first exchange, and the refresh token ROTATES, so a
///     transport-level retry after a slow-but-successful refresh replays a token the server has already spent — the
///     endpoint answers <c>invalid_grant</c> and the operator is forced back through a full re-login.
/// </summary>
/// <remarks>
///     <b>The container under test must have the standard resilience handler installed, or these assertions are
///     vacuous.</b> That is what Aspire's <c>AddServiceDefaults</c> does to every client through
///     <c>ConfigureHttpClientDefaults</c>. Mirrors <c>LocalModelProxyResilienceTests</c>.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class CodexOAuthTokenEndpointResilienceTests
{
    private const string ClientName = AddCodexOAuthProviderExtensions.CodexAuthHttpClientName;

    // Mirrors PipelineNameHelper.GetName(ClientName, "standard") — the options name AddStandardResilienceHandler binds.
    private const string ResilienceOptionsName = ClientName + "-standard";

    [Test]
    public void TokenEndpointClient_UnderAspireDefaults_CarriesNoResilienceHandler()
    {
        using var provider = BuildProvider().Provider;

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ClientName);

        AssertEx.Empty(HandlerChain(handler).OfType<ResilienceHandler>().ToArray(),
            "The OAuth token client must carry no resilience pipeline: a retried refresh replays a rotated, already-consumed "
            + "refresh token and the operator is signed out.");
    }

    [Test]
    public async Task TokenEndpointClient_WhenTheEndpointFails_SendsTheGrantPostExactlyOnce()
    {
        var (provider, upstream) = BuildProvider();
        using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

            using var content = new StringContent("{}");
            using var response = await client.PostAsync(new Uri("https://auth.openai.test/oauth/token"), content);

            AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertEx.Equal(expected: 1, upstream.Attempts, "A single-use OAuth grant must reach the endpoint exactly once.");
        }
    }

    [Test]
    public void TokenEndpointClient_KeepsAFiniteTimeout()
    {
        // The trap this test exists for: AddStandardResilienceHandler sets HttpClient.Timeout to
        // Timeout.InfiniteTimeSpan (its pipeline owns the deadline), and RemoveAllResilienceHandlers removes the
        // HANDLER, not that mutation. A registration that only strips therefore leaves the client under Aspire with no
        // pipeline AND no deadline at all, and a sign-in hangs with nothing left to end it. The registration must put a
        // finite one back — above CodexAuthService's own TokenRequestTimeout, which should be what fires first.
        using var provider = BuildProvider().Provider;

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

        AssertEx.True(client.Timeout != Timeout.InfiniteTimeSpan, "An auth call must not be able to hang forever.");
        AssertEx.True(client.Timeout > new CodexOptions().TokenRequestTimeout,
            $"The backstop ({client.Timeout}) must sit above the per-request token budget, or it fires first.");
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
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var handler = new AttemptCountingHandler();

        // Mirror ServiceDefaults' Aspire branch: a global standard handler on every client, registered BEFORE the
        // production registration so the latter's RemoveAllResilienceHandlers strips it, exactly as in production.
        builder.Services.ConfigureHttpClientDefaults(static http => http.AddStandardResilienceHandler());

        // The registration the composition root calls. Only the HTTP wiring is resolved below, so nothing here needs a
        // data protector, a token store on disk, or a running host.
        _ = builder.AddCodexOAuthProvider(builder.Configuration);

        // Observe the real attempt count, and answer without a socket.
        builder.Services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        // Test-only tuning so a RETRIED attempt (the failure this guards against) adds no real delay to the run.
        // Leaves the retry count and predicate alone — collapsing the backoff must not hide the retry itself.
        builder.Services.Configure<HttpStandardResilienceOptions>(ResilienceOptionsName, static options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });

        return (builder.Services.BuildServiceProvider(), handler);
    }

    /// <summary>Stands in for a token endpoint that answers every attempt with a retryable failure, and counts them.</summary>
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
