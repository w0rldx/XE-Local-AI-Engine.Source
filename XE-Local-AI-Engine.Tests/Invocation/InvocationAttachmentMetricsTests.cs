namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     <c>chat_stream_detached_invocations</c> is an UpDownCounter, so a single unbalanced Add strands it above (or
///     below) zero for the process lifetime and the "leases held with nobody watching" gauge becomes noise. These pin
///     that it returns to zero on BOTH exits — the client comes back, and the run terminalizes — including the ordering
///     that actually happens on every completed turn: the terminal state arrives first, then the hub's <c>finally</c>
///     disposes the attachment.
/// </summary>
[NotInParallel]
public sealed class InvocationAttachmentMetricsTests
{
    private const string DetachedGauge = "chat_stream_detached_invocations";
    private const string ReapedCounter = "chat_detached_invocation_reaped_total";

    [Test]
    public void Detach_ThenReAttach_ReturnsTheGaugeToZero()
    {
        using var capture = new NodeMeterCapture();
        var tracker = CreateTracker(out _);
        var invocationId = Guid.NewGuid();

        var first = tracker.Attach(invocationId);
        AssertEx.Equal(expected: 0L, capture.Net(DetachedGauge), "attaching must not move the gauge");

        first.Dispose();
        AssertEx.Equal(expected: 1L, capture.Net(DetachedGauge), "losing the last consumer counts one detached run");

        using var reattached = tracker.Attach(invocationId);
        AssertEx.Equal(expected: 0L, capture.Net(DetachedGauge), "the client came back");
    }

    [Test]
    public void Detach_ThenTerminalize_ReturnsTheGaugeToZero()
    {
        using var capture = new NodeMeterCapture();
        var tracker = CreateTracker(out var dispatcher);
        var invocationId = Guid.NewGuid();

        tracker.Attach(invocationId).Dispose();
        AssertEx.Equal(expected: 1L, capture.Net(DetachedGauge));

        RaiseTerminal(dispatcher, invocationId);
        AssertEx.Equal(expected: 0L, capture.Net(DetachedGauge), "a turn that ends while detached also releases the gauge");
    }

    [Test]
    public void Terminalize_BeforeTheHubDisposesItsAttachment_LeavesTheGaugeAtZero()
    {
        // The ordinary completion path, and the one that leaks if release is counted unconditionally: the run reports
        // its terminal state (entry removed, never counted) and only then does the hub's finally dispose the handle.
        using var capture = new NodeMeterCapture();
        var tracker = CreateTracker(out var dispatcher);
        var invocationId = Guid.NewGuid();

        var attachment = tracker.Attach(invocationId);
        RaiseTerminal(dispatcher, invocationId);
        attachment.Dispose();

        AssertEx.Equal(expected: 0L, capture.Net(DetachedGauge), "a completed turn must never strand the gauge above zero");
    }

    [Test]
    public async Task Reap_RecordsOneReapPerCancelledInvocation()
    {
        using var capture = new NodeMeterCapture();
        var invocationId = Guid.NewGuid();
        var time = new FakeClock(DateTimeOffset.UnixEpoch);
        var tracker = CreateTracker(out _);
        using var attachment = tracker.Attach(invocationId);
        attachment.Dispose();

        using var reaper = new DetachedInvocationReaper(tracker,
            Substitute.For<IInvocationRunner>(),
            StubNodeRuntimeSettings.Create().WithDetachedGraceSeconds(300).Build(),
            time,
            NullLogger<DetachedInvocationReaper>.Instance);

        time.Advance(TimeSpan.FromSeconds(301));
        await reaper.ReapAsync(CancellationToken.None);
        await reaper.ReapAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1L, capture.Net(ReapedCounter), "the once-only latch must not double-count a reap");
    }

    private static InvocationAttachmentTracker CreateTracker(out IWorkerEventDispatcher dispatcher)
    {
        dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var captured = dispatcher;
        return new InvocationAttachmentTracker(new Lazy<IWorkerEventDispatcher>(() => captured), new FakeClock(DateTimeOffset.UnixEpoch));
    }

    private static void RaiseTerminal(IWorkerEventDispatcher dispatcher, Guid invocationId)
    {
        var state = new InvocationState
        {
            InvocationId = invocationId,
            ConversationId = Guid.NewGuid(),
            Status = InvocationStatus.Completed,
            StreamedContent = string.Empty,
            StreamedThinkingContent = string.Empty,
            StartedAt = DateTimeOffset.UnixEpoch,
            LastUpdatedAt = DateTimeOffset.UnixEpoch
        };
        dispatcher.InvocationStateChanged += Raise.EventWith(dispatcher, new InvocationStateChangedEventArgs(state));
    }

    // Local deterministic clock (repo convention: per-test-file nested fake, no external time-testing package).
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow = _utcNow.Add(timeSpan);
        }
    }

    // Sums every long measurement on one instrument, which for an UpDownCounter IS its current value.
    private sealed class NodeMeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<(string Name, long Value)> _longs = [];

        public NodeMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NodeMetrics.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) => _longs.Add((instrument.Name, measurement)));
            _listener.Start();
        }

        public long Net(string instrumentName)
        {
            return _longs.Where(entry => string.Equals(entry.Name, instrumentName, StringComparison.Ordinal)).Sum(entry => entry.Value);
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
