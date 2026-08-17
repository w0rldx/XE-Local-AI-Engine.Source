namespace XE_Local_AI_Engine.Tests.Invocation;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The reaper is the trigger that ends an abandoned turn. These drive <c>ReapAsync</c> — one tick's work — directly
///     rather than through the <see cref="PeriodicTimer" />, because the repo's fake clocks override only
///     <c>GetUtcNow</c>, so the timer would still run on real time and each assertion would cost five seconds.
/// </summary>
public sealed class DetachedInvocationReaperTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task Reap_PastTheGrace_CancelsTheRunExactlyOnce()
    {
        var invocationId = Guid.NewGuid();
        var time = new FakeClock(Start);
        var tracker = new StubTracker([new DetachedInvocation(invocationId, Start)]);
        var runner = Substitute.For<IInvocationRunner>();
        using var reaper = CreateReaper(tracker, runner, time, graceSeconds: 300);

        time.Advance(TimeSpan.FromSeconds(301));
        await reaper.ReapAsync(CancellationToken.None);
        await reaper.ReapAsync(CancellationToken.None);

        runner.Received(requiredNumberOfCalls: 1).CancelDetached(invocationId);
    }

    [Test]
    public async Task Reap_BeforeTheGraceElapses_DoesNotCancel()
    {
        var invocationId = Guid.NewGuid();
        var time = new FakeClock(Start);
        var tracker = new StubTracker([new DetachedInvocation(invocationId, Start)]);
        var runner = Substitute.For<IInvocationRunner>();
        using var reaper = CreateReaper(tracker, runner, time, graceSeconds: 300);

        time.Advance(TimeSpan.FromSeconds(299));
        await reaper.ReapAsync(CancellationToken.None);

        runner.DidNotReceive().CancelDetached(Arg.Any<Guid>());
    }

    [Test]
    public async Task Reap_WithNothingDetached_CancelsNothing()
    {
        // Covers both the attached run and the run that never attached: neither appears in ListDetached, and the reaper
        // has no other source of candidates.
        var time = new FakeClock(Start);
        var runner = Substitute.For<IInvocationRunner>();
        using var reaper = CreateReaper(new StubTracker([]), runner, time, graceSeconds: 300);

        time.Advance(TimeSpan.FromHours(1));
        await reaper.ReapAsync(CancellationToken.None);

        runner.DidNotReceive().CancelDetached(Arg.Any<Guid>());
    }

    [Test]
    public async Task Reap_WhenTheGraceIsZero_NeverCancels()
    {
        // 0 means "never cancel" — today's behavior, where a detached run is bounded only by the whole-invocation
        // watchdog. It must hold no matter how long the run has been abandoned.
        var invocationId = Guid.NewGuid();
        var time = new FakeClock(Start);
        var tracker = new StubTracker([new DetachedInvocation(invocationId, Start)]);
        var runner = Substitute.For<IInvocationRunner>();
        using var reaper = CreateReaper(tracker, runner, time, graceSeconds: 0);

        time.Advance(TimeSpan.FromDays(1));
        await reaper.ReapAsync(CancellationToken.None);

        runner.DidNotReceive().CancelDetached(Arg.Any<Guid>());
    }

    [Test]
    public async Task Reap_WhenTheOperatorEditsTheGraceMidRun_TakesEffectOnTheNextTick()
    {
        // The direct regression test. Capturing a stored node setting in a singleton field is what silently
        // required a node restart before an operator edit applied; the reaper must re-read the grace on EVERY tick.
        var invocationId = Guid.NewGuid();
        var time = new FakeClock(Start);
        var tracker = new StubTracker([new DetachedInvocation(invocationId, Start)]);
        var runner = Substitute.For<IInvocationRunner>();
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetDetachedGraceSecondsAsync(Arg.Any<CancellationToken>()).Returns(0);
        using var reaper = new DetachedInvocationReaper(tracker, runner, runtimeSettings, time, NullLogger<DetachedInvocationReaper>.Instance);

        time.Advance(TimeSpan.FromSeconds(600));
        await reaper.ReapAsync(CancellationToken.None);
        runner.DidNotReceive().CancelDetached(Arg.Any<Guid>());

        // The operator turns reaping on mid-run. No restart, no new reaper — the very next tick must honour it.
        runtimeSettings.GetDetachedGraceSecondsAsync(Arg.Any<CancellationToken>()).Returns(300);
        await reaper.ReapAsync(CancellationToken.None);

        runner.Received(requiredNumberOfCalls: 1).CancelDetached(invocationId);
    }

    [Test]
    public async Task Reap_AfterAReAttachAndASecondDetach_CanReapAgain()
    {
        // The once-only latch is keyed on the detach INSTANT, not just the id, so a reload that comes back and is
        // abandoned a second time is still reapable rather than permanently immune.
        var invocationId = Guid.NewGuid();
        var time = new FakeClock(Start);
        var tracker = new StubTracker([new DetachedInvocation(invocationId, Start)]);
        var runner = Substitute.For<IInvocationRunner>();
        using var reaper = CreateReaper(tracker, runner, time, graceSeconds: 300);

        time.Advance(TimeSpan.FromSeconds(301));
        await reaper.ReapAsync(CancellationToken.None);

        // Re-attached (absent from the detached set), then detached again at a later instant.
        tracker.Detached = [];
        await reaper.ReapAsync(CancellationToken.None);
        var secondDetachAt = time.GetUtcNow();
        tracker.Detached = [new DetachedInvocation(invocationId, secondDetachAt)];

        time.Advance(TimeSpan.FromSeconds(301));
        await reaper.ReapAsync(CancellationToken.None);

        runner.Received(requiredNumberOfCalls: 2).CancelDetached(invocationId);
    }

    [Test]
    public async Task Reap_CancelsOnlyTheEntriesPastTheGrace()
    {
        var expired = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        var time = new FakeClock(Start);
        var tracker = new StubTracker([
            new DetachedInvocation(expired, Start),
            new DetachedInvocation(fresh, Start.AddSeconds(200))
        ]);
        var runner = Substitute.For<IInvocationRunner>();
        using var reaper = CreateReaper(tracker, runner, time, graceSeconds: 300);

        time.Advance(TimeSpan.FromSeconds(301));
        await reaper.ReapAsync(CancellationToken.None);

        runner.Received(requiredNumberOfCalls: 1).CancelDetached(expired);
        runner.DidNotReceive().CancelDetached(fresh);
    }

    private static DetachedInvocationReaper CreateReaper(IInvocationAttachmentTracker tracker,
        IInvocationRunner runner,
        TimeProvider timeProvider,
        int graceSeconds)
    {
        return new DetachedInvocationReaper(tracker,
            runner,
            StubNodeRuntimeSettings.Create().WithDetachedGraceSeconds(graceSeconds).Build(),
            timeProvider,
            NullLogger<DetachedInvocationReaper>.Instance);
    }

    private sealed class StubTracker(IReadOnlyCollection<DetachedInvocation> detached) : IInvocationAttachmentTracker
    {
        public IReadOnlyCollection<DetachedInvocation> Detached { get; set; } = detached;

        public event EventHandler<InvocationAttachmentChangedEventArgs>? AttachmentChanged;

        public IDisposable Attach(Guid invocationId)
        {
            AttachmentChanged?.Invoke(this, new InvocationAttachmentChangedEventArgs(invocationId, attached: true));
            return new NoopDisposable();
        }

        public bool IsDetached(Guid invocationId)
        {
            return Detached.Any(entry => entry.InvocationId == invocationId);
        }

        public IReadOnlyCollection<DetachedInvocation> ListDetached()
        {
            return Detached;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
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
}
