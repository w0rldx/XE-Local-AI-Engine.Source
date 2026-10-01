namespace XE_Local_AI_Engine.Tests.Invocation;

using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The lifecycle rules that are cheap to state and expensive to get wrong, exercised on the tracker alone rather
///     than through a whole invocation turn: the PRIORITY ORDER of cancellation attribution (a deliberate cancel
///     recorded by its requester beats the caller token, which beats the turn's own watchdog, which beats "nobody of
///     ours fired"), the one-turn-at-a-time admission guard, and the drain fence that stops a new LOCAL turn from
///     slipping in behind the snapshot while a remote assignment is still admitted.
///     <para>
///         The origin cases matter because getting them wrong is not visible in a normal test: the classification is
///         deliberately derived from synchronized state at mapping time rather than from a token callback, and a
///         regression there reads as an occasional wrong failure category rather than as a failure.
///     </para>
/// </summary>
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

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.User, tracker.ResolveCancellationOrigin());
        AssertEx.Equal(FailureCategory.Cancelled, InvocationLifecycleTracker.ClassifyCancellation(tracker.ResolveCancellationOrigin()));
    }

    [Test]
    public async Task ResolveCancellationOrigin_WhenOnlyTheHostTokenFired_ReportsShutdownNotTheWatchdog()
    {
        // Cancelling the caller's token also cancels the linked invocation source, so this pins the ORDER: the captured
        // host token is consulted before the invocation source, otherwise a plain disconnect reads as a timeout.
        var tracker = CreateTracker(new ManualTimeProvider());
        using var hostCancellation = new CancellationTokenSource();
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMinutes(5), hostCancellation.Token);

        await hostCancellation.CancelAsync();

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.Shutdown, tracker.ResolveCancellationOrigin());
    }

    [Test]
    public async Task ResolveCancellationOrigin_WhenOnlyTheTurnWatchdogFired_ReportsWatchdog()
    {
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMilliseconds(1), CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(1));

        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin() == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
            TimeSpan.FromSeconds(5),
            "The turn's own CancelAfter must be attributed to the watchdog once it has fired.");

        AssertEx.Equal(FailureCategory.Timeout, InvocationLifecycleTracker.ClassifyCancellation(tracker.ResolveCancellationOrigin()));
    }

    [Test]
    public void ResolveCancellationOrigin_WhenNothingOfOursIsCancelled_ReportsAProviderTimeout()
    {
        // By elimination: the OperationCanceledException came from below the node (a provider HTTP timeout on a token
        // this node does not own). Calling that an external stop hid a real timeout behind the Cancelled category.
        var tracker = CreateTracker(new ManualTimeProvider());
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMinutes(5), CancellationToken.None);

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.ProviderTimeout, tracker.ResolveCancellationOrigin());
    }

    [Test]
    public void RegisterActiveInvocation_WhileATurnIsActive_RefusesTheSecondTurnUntilTheFirstIsCleared()
    {
        var tracker = CreateTracker(new ManualTimeProvider());
        var firstInvocationId = Guid.NewGuid();
        tracker.RegisterActiveInvocation(firstInvocationId, TimeSpan.FromMinutes(5), CancellationToken.None);

        AssertEx.Throws<InvalidOperationException>(() =>
            tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMinutes(5), CancellationToken.None));
        AssertEx.True(tracker.IsCurrentInvocation(firstInvocationId));

        // A clear for a DIFFERENT turn must not release the slot — the guard is per invocation id, not "any clear".
        tracker.ClearActiveInvocation(Guid.NewGuid());
        AssertEx.True(tracker.IsCurrentInvocation(firstInvocationId));

        tracker.ClearActiveInvocation(firstInvocationId);
        AssertEx.False(tracker.IsCurrentInvocation(firstInvocationId));
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMinutes(5), CancellationToken.None);
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
        // A tool-heavy turn parks once per approval. Each release used to re-arm the FULL InvocationTimeout, so a turn
        // with many approvals ran far past its budget; the model must get back only what it had when the park began.
        var time = new ManualTimeProvider();
        var tracker = CreateTracker(time);
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromSeconds(600), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(400));

        tracker.SetInvocationDeadline(parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(250));
        tracker.SetInvocationDeadline(parkedOnHuman: false);

        time.Advance(TimeSpan.FromSeconds(199));
        AssertEx.Equal(expected: 1, time.ArmedTimerCount, "the human's 250 s must not be charged to the model's remaining 200 s");

        time.Advance(TimeSpan.FromSeconds(1));
        AssertEx.Equal(expected: 0, time.ArmedTimerCount, "the deadline must fire once the remaining 200 s are spent, not a fresh 600 s later");
        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin() == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
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
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(60), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(598));

        tracker.SetInvocationDeadline(parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(30));
        tracker.SetInvocationDeadline(parkedOnHuman: false);

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
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromSeconds(600), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(100));

        tracker.SetInvocationDeadline(parkedOnHuman: true);
        time.Advance(TimeSpan.FromSeconds(899));
        AssertEx.Equal(expected: 1, time.ArmedTimerCount, "an attached park must outlive the model budget it paused");

        time.Advance(TimeSpan.FromSeconds(1));
        AssertEx.Equal(expected: 0, time.ArmedTimerCount, "the parked backstop must fire at MaxPendingToolCallAge + InvocationTimeout");
        await AssertEx.EventuallyAsync(() => tracker.ResolveCancellationOrigin() == InvocationLifecycleTracker.CancellationOrigin.Watchdog,
            TimeSpan.FromSeconds(5),
            "The parked backstop firing must be attributed to the turn watchdog.");
    }

    private static InvocationLifecycleTracker CreateTracker(TimeProvider timeProvider)
    {
        return new InvocationLifecycleTracker(Substitute.For<IInvocationAttachmentTracker>(),
            new PendingToolCallRegistry(),
            StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).Build(),
            timeProvider);
    }
}
