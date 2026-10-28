namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections;
using System.Reflection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Lifecycle rules on the tracker alone: cancel attribution priority (requester, caller token, watchdog, "none of
///     ours"), per-invocation isolation, and the drain fence that keeps a new LOCAL
///     turn out while a remote assignment is admitted.
/// </summary>
/// <remarks>
///     The origin classification is derived from synchronized state at mapping time rather than from a token callback,
///     so a regression there reads as an occasional wrong failure category rather than as a failure.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class InvocationLifecycleTrackerTests
{
    [Test]
    public async Task ResolveCancellationOrigin_WhenAUserCancelRacesTheHostToken_KeepsTheUserOrigin()
    {
        // A deliberate cancel is recorded synchronously by its own requester, so it outranks every derived signal —
        // including a host token that fires immediately afterwards and would otherwise read as a shutdown.
        var tracker = CreateTracker(new ManualTimeProvider());
        var invocationId = Guid.NewGuid();
        using var hostCancellation = new CancellationTokenSource();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromMinutes(5), hostCancellation.Token);

        tracker.Cancel(invocationId);
        await hostCancellation.CancelAsync();

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.User, tracker.ResolveCancellationOrigin(invocationId));
        AssertEx.Equal(FailureCategory.Cancelled, InvocationLifecycleTracker.ClassifyCancellation(tracker.ResolveCancellationOrigin(invocationId)));
    }

    [Test]
    public async Task ResolveCancellationOrigin_WhenOnlyTheHostTokenFired_ReportsShutdownNotTheWatchdog()
    {
        // Cancelling the caller's token also cancels the linked invocation source, so this pins the ORDER: the captured
        // host token is consulted before the invocation source, otherwise a plain disconnect reads as a timeout.
        var tracker = CreateTracker(new ManualTimeProvider());
        using var hostCancellation = new CancellationTokenSource();
        var invocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromMinutes(5), hostCancellation.Token);

        await hostCancellation.CancelAsync();

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.Shutdown, tracker.ResolveCancellationOrigin(invocationId));
    }

    [Test]
    public async Task ResolveCancellationOrigin_WhenOnlyTheTurnWatchdogFired_ReportsWatchdog()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var invocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(1));

        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin(invocationId) == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
            TimeSpan.FromSeconds(5),
            "The turn's own CancelAfter must be attributed to the watchdog once it has fired.");

        AssertEx.Equal(FailureCategory.Timeout, InvocationLifecycleTracker.ClassifyCancellation(tracker.ResolveCancellationOrigin(invocationId)));
    }

    [Test]
    public void ResolveCancellationOrigin_WhenNothingOfOursIsCancelled_ReportsAProviderTimeout()
    {
        // By elimination: the OperationCanceledException came from below the node (a provider HTTP timeout on a token
        // this node does not own). Calling that an external stop hid a real timeout behind the Cancelled category.
        var tracker = CreateTracker(new ManualTimeProvider());
        var invocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromMinutes(5), CancellationToken.None);

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.ProviderTimeout, tracker.ResolveCancellationOrigin(invocationId));
    }

    [Test]
    public void RegisterActiveInvocation_ADuplicateIdThrows_ADifferentIdIsAdmittedAlongside()
    {
        var tracker = CreateTracker(new ManualTimeProvider());
        var firstInvocationId = Guid.NewGuid();
        var secondInvocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(firstInvocationId, TimeSpan.FromMinutes(5), CancellationToken.None);

        AssertEx.Throws<InvalidOperationException>(() =>
            tracker.RegisterActiveInvocation(firstInvocationId, TimeSpan.FromMinutes(5), CancellationToken.None));
        tracker.RegisterActiveInvocation(secondInvocationId, TimeSpan.FromMinutes(5), CancellationToken.None);
        AssertEx.True(tracker.IsActive(firstInvocationId));
        AssertEx.True(tracker.IsActive(secondInvocationId));

        // A clear removes only its own turn.
        tracker.ClearActiveInvocation(firstInvocationId);
        AssertEx.False(tracker.IsActive(firstInvocationId));
        AssertEx.True(tracker.IsActive(secondInvocationId));
    }

    [Test]
    public async Task RegisteredTurns_KeepIndependentDeadlines()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var shortTurn = Guid.NewGuid();
        var longTurn = Guid.NewGuid();
        tracker.RegisterActiveInvocation(shortTurn, TimeSpan.FromSeconds(100), CancellationToken.None);
        tracker.RegisterActiveInvocation(longTurn, TimeSpan.FromSeconds(300), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(100));

        await AssertEx.EventuallyAsync(() => tracker.GetInvocationCancellationToken(shortTurn).IsCancellationRequested,
            TimeSpan.FromSeconds(5),
            "The shorter turn's own deadline must fire.");
        AssertEx.False(tracker.GetInvocationCancellationToken(longTurn).IsCancellationRequested, "the other turn's deadline is its own");
        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.ProviderTimeout, tracker.ResolveCancellationOrigin(longTurn));
    }

    [Test]
    public async Task SetInvocationDeadline_AParkOnOneTurn_LeavesTheOtherTurnsDeadline()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var parked = Guid.NewGuid();
        var running = Guid.NewGuid();
        tracker.RegisterActiveInvocation(parked, TimeSpan.FromSeconds(600), CancellationToken.None);
        tracker.RegisterActiveInvocation(running, TimeSpan.FromSeconds(600), CancellationToken.None);

        tracker.SetInvocationDeadline(parked, parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(600));

        await AssertEx.EventuallyAsync(() => tracker.GetInvocationCancellationToken(running).IsCancellationRequested,
            TimeSpan.FromSeconds(5),
            "The running turn must keep its plain 600 s deadline.");
        AssertEx.False(tracker.GetInvocationCancellationToken(parked).IsCancellationRequested, "only the parked turn gets the park extension");
    }

    [Test]
    public async Task ResolveCancellationOrigin_IsAttributedPerInvocation()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var userCancelled = Guid.NewGuid();
        var timedOut = Guid.NewGuid();
        tracker.RegisterActiveInvocation(userCancelled, TimeSpan.FromSeconds(600), CancellationToken.None);
        tracker.RegisterActiveInvocation(timedOut, TimeSpan.FromSeconds(10), CancellationToken.None);

        tracker.Cancel(userCancelled);
        time.Advance(TimeSpan.FromSeconds(10));

        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin(timedOut) == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
            TimeSpan.FromSeconds(5),
            "The second turn's own watchdog must be attributed to it, not the first turn's user cancel.");
        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.User, tracker.ResolveCancellationOrigin(userCancelled));
    }

    [Test]
    public async Task OnAttachmentChanged_ReArmsOnlyTheMatchingParkedTurn()
    {
        // Both turns park while detached, so each gets the plain backstop. A re-attach of ONE must extend only that one.
        var time = new ManualTimeProvider();
        var attachments = Substitute.For<IInvocationAttachmentTracker>();
        var tracker = CreateTracker(time, attachments);
        var reattached = Guid.NewGuid();
        var stillDetached = Guid.NewGuid();
        attachments.IsDetached(Arg.Any<Guid>()).Returns(returnThis: true);
        tracker.RegisterActiveInvocation(reattached, TimeSpan.FromSeconds(600), CancellationToken.None);
        tracker.RegisterActiveInvocation(stillDetached, TimeSpan.FromSeconds(600), CancellationToken.None);
        tracker.SetInvocationDeadline(reattached, parkedOnHuman: true);
        tracker.SetInvocationDeadline(stillDetached, parkedOnHuman: true);

        attachments.IsDetached(reattached).Returns(returnThis: false);
        attachments.AttachmentChanged += Raise.EventWith(attachments, new InvocationAttachmentChangedEventArgs(reattached, attached: true));
        time.Advance(TimeSpan.FromSeconds(600));

        await AssertEx.EventuallyAsync(() => tracker.GetInvocationCancellationToken(stillDetached).IsCancellationRequested,
            TimeSpan.FromSeconds(5),
            "The detached park keeps its plain backstop.");
        AssertEx.False(tracker.GetInvocationCancellationToken(reattached).IsCancellationRequested, "the re-attached park gets the full park budget");
    }

    [Test]
    public void CancelAll_ReachesEveryRegisteredTurn_AsAShutdown()
    {
        var tracker = CreateTracker(new ManualTimeProvider());
        Guid[] invocationIds = [Guid.NewGuid(), Guid.NewGuid()];
        foreach (var invocationId in invocationIds)
        {
            tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromMinutes(5), CancellationToken.None);
        }

        tracker.CancelAll();

        foreach (var invocationId in invocationIds)
        {
            AssertEx.True(tracker.GetInvocationCancellationToken(invocationId).IsCancellationRequested);
            AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.Shutdown, tracker.ResolveCancellationOrigin(invocationId));
        }
    }

    [Test]
    public void CancelAll_WhenOneSourceWasAlreadyDisposed_StillCancelsTheOthersAndDrainsPendingCalls()
    {
        // A turn cleared between CancelAll's snapshot and its cancel leaves a disposed source in the snapshot. That must not
        // stop the remaining turns' cancel or the pending-call drain.
        var registry = new PendingToolCallRegistry();
        var tracker = CreateTracker(new ManualTimeProvider(), registry: registry);
        var cleared = Guid.NewGuid();
        var running = Guid.NewGuid();
        tracker.RegisterActiveInvocation(cleared, TimeSpan.FromMinutes(5), CancellationToken.None);
        tracker.RegisterActiveInvocation(running, TimeSpan.FromMinutes(5), CancellationToken.None);
        var pendingCall = new PendingToolCall
        {
            InvocationId = running,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ApprovalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        AssertEx.True(registry.Calls.TryAdd("request-1", pendingCall));
        TurnSource(tracker, cleared).Dispose();

        tracker.CancelAll();

        AssertEx.True(tracker.GetInvocationCancellationToken(running).IsCancellationRequested);
        AssertEx.True(pendingCall.ApprovalCompletion.Task.IsCanceled);
        AssertEx.Empty(registry.Calls);
    }

    [Test]
    public async Task DrainActiveInvocationsAsync_FencesLaterAdmission_AndWaitsForTheActiveCompletion()
    {
        var tracker = CreateTracker(new ManualTimeProvider());
        var activeInvocationId = Guid.NewGuid();
        var activeCompletion = AssertEx.NotNull(tracker.RegisterActiveInvocationCompletion(activeInvocationId));
        AssertEx.Equal(expected: 1, tracker.ActiveInvocationCount);

        var drainTask = tracker.DrainActiveInvocationsAsync(TimeSpan.FromSeconds(5));
        AssertEx.False(drainTask.IsCompleted, "The drain must wait for the registered completion.");

        // A turn arriving after the fence is refused, because it would become an untracked run the drain never
        // waits for.
        AssertEx.Null(tracker.RegisterActiveInvocationCompletion(Guid.NewGuid()));

        tracker.CompleteActiveInvocation(activeInvocationId, activeCompletion);
        AssertEx.True(await drainTask);
    }

    [Test]
    public async Task SetInvocationDeadline_ParkThenRelease_DoesNotExtendTheTurnBeyondItsRemainingBudget()
    {
        // A tool-heavy turn parks once per approval. Re-arming the FULL InvocationTimeout per release would let a turn
        // with many approvals run far past its budget; the model must get back only what it had when the park began.
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var invocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromSeconds(600), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(400));

        tracker.SetInvocationDeadline(invocationId, parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(250));
        tracker.SetInvocationDeadline(invocationId, parkedOnHuman: false);

        time.Advance(TimeSpan.FromSeconds(199));
        AssertEx.Equal(expected: 1, time.ArmedTimerCount, "the human's 250 s must not be charged to the model's remaining 200 s");

        time.Advance(TimeSpan.FromSeconds(1));
        AssertEx.Equal(expected: 0, time.ArmedTimerCount, "the deadline must fire once the remaining 200 s are spent, not a fresh 600 s later");
        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin(invocationId) == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
            TimeSpan.FromSeconds(5),
            "The resumed budget running out must be attributed to the turn watchdog.");
    }

    [Test]
    public void SetInvocationDeadline_ReleaseWithAlmostNoBudgetLeft_ReArmsAtLeastOneStreamIdleWindow()
    {
        // An approval granted when the model had ~2 s left must not be answered by an immediate Timeout: the release
        // re-arms at least one stream-idle window (60 s here) so the tool result and one model round can land.
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var invocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(60), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(598));

        tracker.SetInvocationDeadline(invocationId, parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.SetInvocationDeadline(invocationId, parkedOnHuman: false);

        time.Advance(TimeSpan.FromSeconds(59));
        AssertEx.Equal(expected: 1, time.ArmedTimerCount, "the release must re-arm the 60 s floor, not the 2 s the model had left");

        time.Advance(TimeSpan.FromSeconds(1));
        AssertEx.Equal(expected: 0, time.ArmedTimerCount, "the floor is a floor, not a fresh turn budget: it fires after one idle window");
    }

    [Test]
    public async Task SetInvocationDeadline_WhileParked_TheParkedBackstopStillApplies()
    {
        // The park pushes the deadline to MaxPendingToolCallAge + InvocationTimeout (5 min + 600 s here), never further:
        // a wait whose own cap was somehow missed still ends the turn.
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        var invocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(invocationId, TimeSpan.FromSeconds(600), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(100));

        tracker.SetInvocationDeadline(invocationId, parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(899));
        AssertEx.Equal(expected: 1, time.ArmedTimerCount, "an attached park must outlive the model budget it paused");

        time.Advance(TimeSpan.FromSeconds(1));
        AssertEx.Equal(expected: 0, time.ArmedTimerCount, "the parked backstop must fire at MaxPendingToolCallAge + InvocationTimeout");
        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin(invocationId) == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
            TimeSpan.FromSeconds(5),
            "The parked backstop firing must be attributed to the turn watchdog.");
    }

    // The registered turn's linked source, reached by reflection: no member hands it out, and the test must dispose it in place.
    private static CancellationTokenSource TurnSource(InvocationLifecycleTracker tracker, Guid invocationId)
    {
        var field = AssertEx.NotNull(typeof(InvocationLifecycleTracker).GetField("_turns", BindingFlags.Instance | BindingFlags.NonPublic));
        var turn = AssertEx.NotNull(((IDictionary)AssertEx.NotNull(field.GetValue(tracker)))[invocationId]);
        return (CancellationTokenSource)AssertEx.NotNull(AssertEx.NotNull(turn.GetType().GetProperty("Source")).GetValue(turn));
    }

    private static InvocationLifecycleTracker CreateTracker(TimeProvider timeProvider,
        IInvocationAttachmentTracker? attachmentTracker = null,
        PendingToolCallRegistry? registry = null)
    {
        return new InvocationLifecycleTracker(attachmentTracker ?? Substitute.For<IInvocationAttachmentTracker>(),
            registry ?? new PendingToolCallRegistry(),
            StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).Build(),
            timeProvider);
    }
}
