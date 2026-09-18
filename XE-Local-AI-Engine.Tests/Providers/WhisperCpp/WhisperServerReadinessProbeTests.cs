namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Net;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Readiness is the health route and nothing else. The case that earns this class is the 503: the server answers
///     it while a model is loading, including for the whole of an in-place model switch, so a 503 has to keep the loop
///     polling rather than read as a failure the way any other non-success would.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WhisperServerReadinessProbeTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18300/");

    [Test]
    public async Task WaitForReady_PortRefusedThenHealthy_ReportsReady()
    {
        // Two connection failures (the socket is not bound yet while the process starts), then a 200.
        using var handler = new SequenceHandler(callIndex => callIndex < 2
            ? throw new HttpRequestException("Connection refused.")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());

        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromSeconds(5), CancellationToken.None);

        AssertEx.True(ready);
        AssertEx.True(handler.CallCount >= 3, "The probe must retry a refused connection before succeeding.");
        AssertEx.Equal("/health", handler.LastPath, "Readiness must probe the health route.");
    }

    [Test]
    public async Task WaitForReady_HealthReturns503UntilDeadline_ReportsNotReady()
    {
        // 503 means the process is up and loading. It is retried, not treated as a failure — and when the budget runs
        // out the answer is an honest "not ready" rather than an exception.
        using var handler = new SequenceHandler(static _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());

        // real-timer: the probe takes no TimeProvider — its poll interval and its deadline are both CancelAfter on the
        // real clock — so a fake clock cannot drive it. The budget is 400 ms, deliberately the smallest that still
        // lets at least one poll land.
        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        AssertEx.False(ready);
        AssertEx.True(handler.CallCount >= 1, "A 503 must be polled, not abandoned on the first response.");
    }

    [Test]
    public async Task WaitForReady_HealthReturns503ThenHealthy_ReportsReady()
    {
        using var handler = new SequenceHandler(callIndex => callIndex < 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());

        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromSeconds(5), CancellationToken.None);

        AssertEx.True(ready, "A model that finishes loading must be reported ready.");
    }

    [Test]
    public async Task WaitForReady_NeverAnswers_ReturnsFalseAtDeadline()
    {
        using var handler = new SequenceHandler(static _ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());

        // real-timer: as above — the probe reads the real clock, so the deadline is the only thing that can end this
        // wait, and expressing it needs a real interval.
        var ready = await probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        AssertEx.False(ready);
    }

    [Test]
    public async Task CheckResponsive_NonSuccess_ReturnsFalse()
    {
        using var handler = new SequenceHandler(static _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());

        AssertEx.False(await probe.CheckResponsiveAsync(BaseAddress, CancellationToken.None));
        AssertEx.Equal(expected: 1, handler.CallCount, "The liveness check is one shot, never a poll.");
    }

    [Test]
    public async Task CheckResponsive_Healthy_ReturnsTrue()
    {
        using var handler = new SequenceHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());

        AssertEx.True(await probe.CheckResponsiveAsync(BaseAddress, CancellationToken.None));
    }

    [Test]
    public async Task WaitForReady_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        // The per-probe budget must not swallow the CALLER's cancellation: a shutdown has to unwind, not be reported
        // as an ordinary not-ready.
        using var handler = new SequenceHandler(static _ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => probe.WaitForReadyAsync(BaseAddress, TimeSpan.FromSeconds(5), cts.Token));
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;

        public SequenceHandler(Func<int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount { get; private set; }

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            var index = CallCount;
            CallCount++;
            return Task.FromResult(_responder(index));
        }
    }
}
