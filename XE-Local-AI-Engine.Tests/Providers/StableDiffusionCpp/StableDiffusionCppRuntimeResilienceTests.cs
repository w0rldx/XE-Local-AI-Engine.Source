namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Tests.Testing;

// MED-013: the sd-server runtime HttpClient must
//  - NOT retry the state-changing job-submit POST (SdServerJobClient.SubmitAsync carries no idempotency key), and
//  - retry safe methods (GET poll/readiness), while
//  - keeping the attempt/total timeout and circuit breaker for all methods, and
//  - remaining a SINGLE pipeline even under Aspire, where ServiceDefaults' ConfigureHttpClientDefaults would otherwise
//    add a second, POST-retrying standard handler. Mirrors CentralPlatformResilienceTests.
public sealed class StableDiffusionCppRuntimeResilienceTests
{
    private const string ClientName = StableDiffusionCppRuntimeServiceCollectionExtensions.RuntimeHttpClientName;

    // Mirrors PipelineNameHelper.GetName(ClientName, "standard") — the options name AddStandardResilienceHandler binds.
    private const string ResilienceOptionsName = ClientName + "-standard";

    [Test]
    public async Task Post_TransientFailure_IsNotRetried()
    {
        var (factory, handler) = BuildClient(simulateAspireGlobalHandler: false, _ => ServiceUnavailable());

        using var response = await SendRequestAsync(factory, HttpMethod.Post);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.Equal(expected: 1, handler.Attempts, "A transient job-submit POST must not be retried (duplicate-job hazard).");
    }

    [Test]
    public async Task Get_TransientFailure_IsRetried()
    {
        var (factory, handler) = BuildClient(simulateAspireGlobalHandler: false, _ => ServiceUnavailable());

        using var response = await SendRequestAsync(factory, HttpMethod.Get);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // 1 initial attempt + the standard handler's 3 retries.
        AssertEx.Equal(expected: 4, handler.Attempts, "A transient GET (safe poll/readiness method) must be retried.");
    }

    [Test]
    public async Task UnderSimulatedAspire_Post_IsNotDoubleRetried()
    {
        var (factory, handler) = BuildClient(simulateAspireGlobalHandler: true, _ => ServiceUnavailable());

        using var response = await SendRequestAsync(factory, HttpMethod.Post);

        AssertEx.Equal(expected: 1, handler.Attempts, "The global Aspire handler must not double-wrap this client into retrying the submit POST.");
    }

    [Test]
    public async Task UnderSimulatedAspire_Get_RetriesExactlyOnePipeline()
    {
        // A doubled (nested) pipeline would multiply attempts to (1+3)*(1+3)=16; a single owned pipeline yields 1+3=4.
        var (factory, handler) = BuildClient(simulateAspireGlobalHandler: true, _ => ServiceUnavailable());

        using var response = await SendRequestAsync(factory, HttpMethod.Get);

        AssertEx.Equal(expected: 4, handler.Attempts, "Exactly one resilience pipeline must apply, not two nested ones.");
    }

    [Test]
    public void OwnedPipeline_PreservesTimeoutAndCircuitBreakerForAllMethods()
    {
        var (provider, _) = BuildProvider(simulateAspireGlobalHandler: false, _ => ServiceUnavailable());

        var options = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>().Get(ResilienceOptionsName);

        AssertEx.True(options.AttemptTimeout.Timeout > TimeSpan.Zero, "Per-attempt timeout must be preserved.");
        AssertEx.True(options.TotalRequestTimeout.Timeout > options.AttemptTimeout.Timeout, "Total-request timeout must be preserved.");
        AssertEx.True(options.CircuitBreaker.FailureRatio > 0, "Circuit breaker must be preserved for all methods.");
        AssertEx.NotNull(options.Retry.ShouldHandle, "Retry predicate (method-gated by DisableForUnsafeHttpMethods) must be present.");
    }

    private static async Task<HttpResponseMessage> SendRequestAsync(IHttpClientFactory factory, HttpMethod method)
    {
        var client = factory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(method, "http://127.0.0.1:5000/generate");
        return await client.SendAsync(request);
    }

    private static HttpResponseMessage ServiceUnavailable() =>
        new(HttpStatusCode.ServiceUnavailable);

    private static (IHttpClientFactory factory, AttemptCountingHandler handler) BuildClient(bool simulateAspireGlobalHandler,
        Func<int, HttpResponseMessage> responder)
    {
        var (provider, handler) = BuildProvider(simulateAspireGlobalHandler, responder);
        return (provider.GetRequiredService<IHttpClientFactory>(), handler);
    }

    private static (ServiceProvider provider, AttemptCountingHandler handler) BuildProvider(bool simulateAspireGlobalHandler,
        Func<int, HttpResponseMessage> responder)
    {
        var services = new ServiceCollection();
        var handler = new AttemptCountingHandler(responder);

        if (simulateAspireGlobalHandler)
        {
            // Mirror ServiceDefaults' Aspire branch: a global standard handler applied to every client. It is registered
            // BEFORE the runtime extension so the extension's RemoveAllResilienceHandlers strips it, exactly as in prod.
            services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        }

        // The production registration path owns the POST-safe pipeline for the sdcpp-runtime client.
        services.AddStableDiffusionCppImageRuntime();

        // Swap the primary handler for the counting handler so the assertions observe real attempt counts.
        services.AddHttpClient(ClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        // Test-only tuning: collapse retry backoff so retried attempts add no real delay. Leaves ShouldHandle (the
        // production method-gating from DisableForUnsafeHttpMethods) and MaxRetryAttempts untouched.
        services.Configure<HttpStandardResilienceOptions>(ResilienceOptionsName, options =>
        {
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
        });

        return (services.BuildServiceProvider(), handler);
    }

    private sealed class AttemptCountingHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        private int _attempts;

        public AttemptCountingHandler(Func<int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int Attempts => Volatile.Read(ref _attempts);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref _attempts);
            return Task.FromResult(_responder(attempt));
        }
    }
}
