namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The readiness/liveness probe must issue exactly ONE request per attempt — no exponential retries — so the
///     supervisor's poll cadence, not a resilience handler, controls readiness-detection timing. These tests drive the
///     probe over a counting message handler (the seam the DI change swaps for a dedicated, resilience-free client).
/// </summary>
public sealed class LlamaServerHealthProbeTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18100/v1");

    [Test]
    public async Task CheckResponsive_Ready_IssuesExactlyOneRequest()
    {
        using var handler = new CountingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(handler, disposeHandler: false);
        var probe = new LlamaServerHealthProbe(client);

        var responsive = await probe.CheckResponsiveAsync(BaseAddress, CancellationToken.None);

        AssertEx.True(responsive, "A 200 /health should report responsive.");
        AssertEx.Equal(expected: 1, handler.Count); // exactly one request — no retries.
    }

    [Test]
    public async Task CheckResponsive_ServerError_IssuesExactlyOneRequest_NoRetry()
    {
        using var handler = new CountingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler, disposeHandler: false);
        var probe = new LlamaServerHealthProbe(client);

        var responsive = await probe.CheckResponsiveAsync(BaseAddress, CancellationToken.None);

        AssertEx.False(responsive, "A 503 /health should report not-responsive.");
        AssertEx.Equal(expected: 1, handler.Count); // a failing probe is NOT retried — one request.
    }

    [Test]
    public async Task WaitForReady_PollsOncePerAttempt_UntilReady()
    {
        // Not-up (503) twice, then ready (200): each poll is exactly one request, so readiness after 3 polls == 3 requests.
        var responses = new Queue<HttpStatusCode>([HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK]);
        using var handler = new CountingHandler((_, _) =>
        {
            var status = responses.Count > 0 ? responses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status));
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var probe = new LlamaServerHealthProbe(client);

        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromSeconds(5), CancellationToken.None);

        AssertEx.True(ready, "The server became ready within the budget.");
        AssertEx.Equal(expected: 3, handler.Count); // one request per poll, no per-poll retries.
    }

    [Test]
    public async Task WaitForReadyAndVerifyModelAlias_ExactAliasOnModelsEndpoint_ReturnsTrue()
    {
        Uri? requestedUri = null;
        using var handler = new CountingHandler((request, _) =>
        {
            requestedUri = request.RequestUri;
            return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"xe-evaluation-owned"}]}"""));
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var probe = new LlamaServerHealthProbe(client);

        var ready = await probe.WaitForReadyAndVerifyModelAliasAsync(BaseAddress,
            "xe-evaluation-owned",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        AssertEx.True(ready);
        AssertEx.Equal("http://127.0.0.1:18100/v1/models", requestedUri?.AbsoluteUri);
        AssertEx.Equal(expected: 1, handler.Count);
    }

    [Test]
    public async Task WaitForReadyAndVerifyModelAlias_DifferentProcessOnReleasedPort_FailsClosed()
    {
        using var handler = new CountingHandler((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"some-other-process"}]}""")));
        using var client = new HttpClient(handler, disposeHandler: false);
        var probe = new LlamaServerHealthProbe(client);

        var ready = await probe.WaitForReadyAndVerifyModelAliasAsync(BaseAddress,
            "xe-evaluation-owned",
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        AssertEx.False(ready);
        AssertEx.Equal(expected: 1, handler.Count);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class CountingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return responder(request, cancellationToken);
        }
    }
}
