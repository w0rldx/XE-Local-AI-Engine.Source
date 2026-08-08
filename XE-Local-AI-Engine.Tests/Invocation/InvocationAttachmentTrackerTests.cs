namespace XE_Local_AI_Engine.Tests.Invocation;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The tracker answers one question — "does this run still have somebody watching it?" — and two consumers depend
///     on the answer: the disconnect reaper and the runner's park deadline. The load-bearing property is that a run
///     which NEVER attached is invisible to both, so a scheduled or platform run is neither reaped nor stripped of its
///     park budget.
/// </summary>
public sealed class InvocationAttachmentTrackerTests
{
    [Test]
    public void Attach_ThenDispose_MarksDetachedAndStampsTheInstant()
    {
        var time = new FakeClock(DateTimeOffset.UnixEpoch);
        var tracker = CreateTracker(out _, time);
        var invocationId = Guid.NewGuid();

        var attachment = tracker.Attach(invocationId);
        AssertEx.False(tracker.IsDetached(invocationId), "an attached invocation is not detached");
        AssertEx.Empty(tracker.ListDetached());

        time.Advance(TimeSpan.FromSeconds(30));
        attachment.Dispose();

        AssertEx.True(tracker.IsDetached(invocationId));
        var detached = AssertEx.NotNull(tracker.ListDetached().SingleOrDefault());
        AssertEx.Equal(invocationId, detached.InvocationId);
        AssertEx.Equal(DateTimeOffset.UnixEpoch.AddSeconds(30), detached.DetachedAtUtc);
    }

    [Test]
    public void IsDetached_ForAnInvocationThatNeverAttached_IsFalse()
    {
        // The load-bearing asymmetry. A scheduled agent run, a platform-hub run, or an MCP agent run never streams
        // through LocalChatHub, so it has no entry here — and must NOT be treated as an abandoned turn.
        var tracker = CreateTracker(out _, new FakeClock(DateTimeOffset.UnixEpoch));

        AssertEx.False(tracker.IsDetached(Guid.NewGuid()));
        AssertEx.Empty(tracker.ListDetached());
    }

    [Test]
    public void Attach_IsRefCounted_SoOnlyTheLastDisposeDetaches()
    {
        // A reconnect racing the original stream holds two handles at once; the first one going away must not make the
        // run look abandoned while the second is still rendering it.
        var tracker = CreateTracker(out _, new FakeClock(DateTimeOffset.UnixEpoch));
        var invocationId = Guid.NewGuid();

        var first = tracker.Attach(invocationId);
        var second = tracker.Attach(invocationId);

        first.Dispose();
        AssertEx.False(tracker.IsDetached(invocationId), "one remaining consumer still counts as attached");

        second.Dispose();
        AssertEx.True(tracker.IsDetached(invocationId));
    }

    [Test]
    public void Dispose_Twice_DoesNotDoubleRelease()
    {
        // The hub disposes in a finally a faulted enumerator can reach more than once; a double release would drop the
        // count below the number of live consumers and report an attached run as abandoned.
        var tracker = CreateTracker(out _, new FakeClock(DateTimeOffset.UnixEpoch));
        var invocationId = Guid.NewGuid();

        var first = tracker.Attach(invocationId);
        var second = tracker.Attach(invocationId);

        first.Dispose();
        first.Dispose();

        AssertEx.False(tracker.IsDetached(invocationId), "the second consumer is still attached");
        second.Dispose();
        AssertEx.True(tracker.IsDetached(invocationId));
    }

    [Test]
    public void ReAttach_ClearsTheDetachedStamp()
    {
        var time = new FakeClock(DateTimeOffset.UnixEpoch);
        var tracker = CreateTracker(out _, time);
        var invocationId = Guid.NewGuid();

        tracker.Attach(invocationId).Dispose();
        AssertEx.True(tracker.IsDetached(invocationId));

        time.Advance(TimeSpan.FromSeconds(10));
        var reattached = tracker.Attach(invocationId);

        AssertEx.False(tracker.IsDetached(invocationId));
        AssertEx.Empty(tracker.ListDetached());

        // And the re-detach re-stamps from NOW, not from the original detach: the grace restarts on the second loss.
        time.Advance(TimeSpan.FromSeconds(5));
        reattached.Dispose();
        AssertEx.Equal(DateTimeOffset.UnixEpoch.AddSeconds(15), tracker.ListDetached().Single().DetachedAtUtc);
    }

    [Test]
    public void AttachmentChanged_FiresOnlyOnTheZeroBoundaries()
    {
        var tracker = CreateTracker(out _, new FakeClock(DateTimeOffset.UnixEpoch));
        var invocationId = Guid.NewGuid();
        var changes = new List<bool>();
        tracker.AttachmentChanged += (_, args) =>
        {
            AssertEx.Equal(invocationId, args.InvocationId);
            changes.Add(args.Attached);
        };

        var first = tracker.Attach(invocationId);
        var second = tracker.Attach(invocationId);
        first.Dispose();
        second.Dispose();

        AssertEx.Equal(expected: 2, changes.Count, "the intermediate one-to-two and two-to-one moves are not boundaries");
        AssertEx.True(changes[0]);
        AssertEx.False(changes[1]);
    }

    [Test]
    public void TerminalInvocationState_RemovesTheEntry()
    {
        // Otherwise every completed turn that was ever watched lingers in ListDetached for the process lifetime, and the
        // reaper keeps re-examining runs that can no longer be cancelled.
        var tracker = CreateTracker(out var dispatcher, new FakeClock(DateTimeOffset.UnixEpoch));
        var invocationId = Guid.NewGuid();
        tracker.Attach(invocationId).Dispose();
        AssertEx.True(tracker.IsDetached(invocationId));

        RaiseState(dispatcher, invocationId, InvocationStatus.Completed);

        AssertEx.False(tracker.IsDetached(invocationId));
        AssertEx.Empty(tracker.ListDetached());
    }

    [Test]
    public void NonTerminalInvocationState_KeepsTheEntry()
    {
        var tracker = CreateTracker(out var dispatcher, new FakeClock(DateTimeOffset.UnixEpoch));
        var invocationId = Guid.NewGuid();
        tracker.Attach(invocationId).Dispose();

        RaiseState(dispatcher, invocationId, InvocationStatus.Running);

        AssertEx.True(tracker.IsDetached(invocationId), "a still-running detached turn is exactly what the reaper is for");
    }

    [Test]
    public async Task Attach_UnderConcurrency_CountsEveryConsumer()
    {
        var tracker = CreateTracker(out _, new FakeClock(DateTimeOffset.UnixEpoch));
        var invocationId = Guid.NewGuid();

        var handles = await Task.WhenAll(Enumerable.Range(start: 0, count: 64)
                                                   .Select(_ => Task.Run(() => tracker.Attach(invocationId))));

        AssertEx.False(tracker.IsDetached(invocationId));

        await Task.WhenAll(handles.Select(handle => Task.Run(handle.Dispose)));

        AssertEx.True(tracker.IsDetached(invocationId), "every attach must be matched by exactly one release");
    }

    private static InvocationAttachmentTracker CreateTracker(out IWorkerEventDispatcher dispatcher, TimeProvider timeProvider)
    {
        dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var captured = dispatcher;
        return new InvocationAttachmentTracker(new Lazy<IWorkerEventDispatcher>(() => captured), timeProvider);
    }

    private static void RaiseState(IWorkerEventDispatcher dispatcher, Guid invocationId, InvocationStatus status)
    {
        var state = new InvocationState
        {
            InvocationId = invocationId,
            ConversationId = Guid.NewGuid(),
            Status = status,
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
}
