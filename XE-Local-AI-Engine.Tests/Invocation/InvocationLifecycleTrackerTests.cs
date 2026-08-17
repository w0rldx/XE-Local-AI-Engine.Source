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
public sealed class InvocationLifecycleTrackerTests
{
    [Test]
    public async Task ResolveCancellationOrigin_WhenAUserCancelRacesTheHostToken_KeepsTheUserOrigin()
    {
        // A deliberate cancel is recorded synchronously by its own requester, so it outranks every derived signal —
        // including a host token that fires immediately afterwards and would otherwise read as a shutdown.
        var tracker = CreateTracker();
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
        var tracker = CreateTracker();
        using var hostCancellation = new CancellationTokenSource();
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMinutes(5), hostCancellation.Token);

        await hostCancellation.CancelAsync();

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.Shutdown, tracker.ResolveCancellationOrigin());
    }

    [Test]
    public async Task ResolveCancellationOrigin_WhenOnlyTheTurnWatchdogFired_ReportsWatchdog()
    {
        var tracker = CreateTracker();
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMilliseconds(1), CancellationToken.None);

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
        var tracker = CreateTracker();
        tracker.RegisterActiveInvocation(Guid.NewGuid(), TimeSpan.FromMinutes(5), CancellationToken.None);

        AssertEx.Equal(InvocationLifecycleTracker.CancellationOrigin.ProviderTimeout, tracker.ResolveCancellationOrigin());
    }

    [Test]
    public void RegisterActiveInvocation_WhileATurnIsActive_RefusesTheSecondTurnUntilTheFirstIsCleared()
    {
        var tracker = CreateTracker();
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
    public async Task DrainActiveInvocationsAsync_FencesLocalAdmissionOnly_AndWaitsForTheActiveCompletion()
    {
        var tracker = CreateTracker();
        var activeInvocationId = Guid.NewGuid();
        var activeCompletion = AssertEx.NotNull(tracker.RegisterActiveInvocationCompletion(activeInvocationId, isLocalLoopback: true));
        AssertEx.Equal(expected: 1, tracker.ActiveInvocationCount);

        var drainTask = tracker.DrainActiveInvocationsAsync(TimeSpan.FromSeconds(5));
        AssertEx.False(drainTask.IsCompleted, "The drain must wait for the registered completion.");

        // A local turn arriving after the fence is refused, because it would become an untracked run the drain never
        // waits for. A remote assignment is not fenced here — the dispatcher already stopped accepting those at drain.
        AssertEx.Null(tracker.RegisterActiveInvocationCompletion(Guid.NewGuid(), isLocalLoopback: true));
        AssertEx.NotNull(tracker.RegisterActiveInvocationCompletion(Guid.NewGuid(), isLocalLoopback: false));

        tracker.CompleteActiveInvocation(activeInvocationId, activeCompletion);
        AssertEx.True(await drainTask);
    }

    private static InvocationLifecycleTracker CreateTracker()
    {
        return new InvocationLifecycleTracker(Substitute.For<IInvocationAttachmentTracker>(),
            new PendingToolCallRegistry(),
            StubNodeRuntimeSettings.Create().WithMaxPendingToolCallAgeMinutes(5).Build());
    }
}
