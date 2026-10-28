namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Net;
using System.Threading.Channels;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Readiness is the health route and nothing else. The 503 earns this class: the server answers it while a model
///     loads, including an in-place model switch, so a 503 keeps the loop polling instead of failing.
/// </summary>
/// <remarks>
///     The probe's poll pause and both deadlines run on a <see cref="ManualTimeProvider" />: a test moves the clock past
///     one poll only after that poll's timer is armed, and moves it past the deadline to end a wait.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class WhisperServerReadinessProbeTests
{
    private static readonly Uri BaseAddress = new("http://127.0.0.1:18300/");

    private static readonly TimeSpan ReadinessBudget = TimeSpan.FromMinutes(5);

    [Test]
    public async Task WaitForReady_PortRefusedThenHealthy_ReportsReady()
    {
        // Two connection failures (the socket is not bound yet while the process starts), then a 200.
        using var handler = new SequenceHandler(callIndex => callIndex < 2
            ? throw new HttpRequestException("Connection refused.")
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var clock = new PollSignallingClock();
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), clock);

        var wait = probe.WaitForReadyAsync(BaseAddress, ReadinessBudget, CancellationToken.None);
        await clock.AdvancePastNextPollAsync();
        await clock.AdvancePastNextPollAsync();

        AssertEx.True(await wait);
        AssertEx.Equal(expected: 3, handler.CallCount, "The probe must retry a refused connection before succeeding.");
        AssertEx.Equal("/health", handler.LastPath, "Readiness must probe the health route.");
    }

    [Test]
    public async Task WaitForReady_HealthReturns503UntilDeadline_ReportsNotReady()
    {
        // 503 means the process is up and loading. It is retried, not treated as a failure — and when the budget runs
        // out the answer is an honest "not ready" rather than an exception.
        using var handler = new SequenceHandler(static _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler, disposeHandler: false);
        var clock = new PollSignallingClock();
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), clock);

        var wait = probe.WaitForReadyAsync(BaseAddress, ReadinessBudget, CancellationToken.None);
        await clock.AdvancePastNextPollAsync();
        await clock.AdvancePastNextPollAsync();
        clock.Advance(ReadinessBudget);

        AssertEx.False(await wait);
        AssertEx.True(handler.CallCount >= 2, "A 503 must be polled, not abandoned on the first response.");
    }

    [Test]
    public async Task WaitForReady_HealthReturns503ThenHealthy_ReportsReady()
    {
        using var handler = new SequenceHandler(callIndex => callIndex < 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var clock = new PollSignallingClock();
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), clock);

        var wait = probe.WaitForReadyAsync(BaseAddress, ReadinessBudget, CancellationToken.None);
        await clock.AdvancePastNextPollAsync();
        await clock.AdvancePastNextPollAsync();

        AssertEx.True(await wait, "A model that finishes loading must be reported ready.");
    }

    [Test]
    public async Task WaitForReady_NeverAnswers_ReturnsFalseAtDeadline()
    {
        using var handler = new SequenceHandler(static _ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var clock = new PollSignallingClock();
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), clock);

        var wait = probe.WaitForReadyAsync(BaseAddress, ReadinessBudget, CancellationToken.None);
        await clock.AdvancePastNextPollAsync();
        clock.Advance(ReadinessBudget);

        AssertEx.False(await wait);
    }

    [Test]
    public async Task CheckResponsive_NonSuccess_ReturnsFalse()
    {
        using var handler = new SequenceHandler(static _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), new ManualTimeProvider());

        AssertEx.False(await probe.CheckResponsiveAsync(BaseAddress, CancellationToken.None));
        AssertEx.Equal(expected: 1, handler.CallCount, "The liveness check is one shot, never a poll.");
    }

    [Test]
    public async Task CheckResponsive_Healthy_ReturnsTrue()
    {
        using var handler = new SequenceHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), new ManualTimeProvider());

        AssertEx.True(await probe.CheckResponsiveAsync(BaseAddress, CancellationToken.None));
    }

    [Test]
    public async Task WaitForReady_WhenTheCallerCancels_PropagatesTheCancellation()
    {
        // The per-probe budget must not swallow the CALLER's cancellation: a shutdown has to unwind, not be reported
        // as an ordinary not-ready.
        using var handler = new SequenceHandler(static _ => throw new HttpRequestException("Connection refused."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var probe = new WhisperServerReadinessProbe(http, new WhisperRuntimeOptions(), new ManualTimeProvider());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => probe.WaitForReadyAsync(BaseAddress, ReadinessBudget, cts.Token));
    }

    /// <summary>
    ///     A <see cref="ManualTimeProvider" /> that counts the poll pauses the probe arms, so a test advances past a
    ///     poll only once it is actually waiting on one.
    /// </summary>
    private sealed class PollSignallingClock : TimeProvider
    {
        private readonly ManualTimeProvider _inner = new();
        private readonly Channel<TimeSpan> _pollsArmed = Channel.CreateUnbounded<TimeSpan>();

        public override long TimestampFrequency => _inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() =>
            _inner.GetUtcNow();

        public override long GetTimestamp() =>
            _inner.GetTimestamp();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == WhisperServerReadinessProbe.PollInterval)
            {
                AssertEx.True(_pollsArmed.Writer.TryWrite(dueTime));
            }

            return timer;
        }

        public void Advance(TimeSpan delta) =>
            _inner.Advance(delta);

        public async Task AdvancePastNextPollAsync()
        {
            var pause = await _pollsArmed.Reader.ReadAsync().AsTask().WaitAsync(TestBudgets.Contended);
            _inner.Advance(pause);
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responder;
        private int _callCount;

        public SequenceHandler(Func<int, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            var index = Interlocked.Increment(ref _callCount) - 1;
            return Task.FromResult(_responder(index));
        }
    }
}
