namespace XE_Local_AI_Engine.Tests.Chat;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     A turn parked on an approval or a question gives the node's one invocation slot back, so a send in another
///     conversation runs at once, and the parked turn takes the slot again when it resumes. Driven through the real
///     <see cref="WorkerEventDispatcher" /> slot and a real <see cref="HumanPark" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationSlotAdmissionTests
{
    [Test]
    public async Task AcquireAsync_WhileAnotherConversationsTurnIsParked_IsAdmittedAtOnce_WithNoParkedNotice()
    {
        var harness = new Harness();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        harness.Dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var parked = Package();
        var running = Package();
        await using var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        harness.Park(parked).Park();

        var acquire = InvocationSlotAdmission.AcquireAsync(harness.Dispatcher, running, CancellationToken.None);

        AssertEx.True(acquire.IsCompleted, "a parked turn holds no slot, so the send is admitted without waiting");
        await using var runningLease = await acquire;
        AssertEx.Equal(running.InvocationId, AssertEx.NotNull(harness.Dispatcher.SlotHolder).InvocationId);
        AssertEx.False(notices.Any(static payload => payload.Kind == TurnNoticeKind.QueuedBehindParkedTurn), "the parked-turn notice is no longer emitted");
    }

    [Test]
    public async Task ResumeAsync_WaitsUntilTheRunningTurnReleasesTheSlot_ThenHoldsIt()
    {
        var harness = new Harness();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        harness.Dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var parked = Package();
        var running = Package();
        await using var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        var park = harness.Park(parked);
        park.Park();
        var runningLease = await InvocationSlotAdmission.AcquireAsync(harness.Dispatcher, running, CancellationToken.None);

        var resume = park.ResumeAsync(CancellationToken.None);
        AssertEx.False(resume.IsCompleted, "the answered turn waits for the running one");

        await harness.Dispatcher.ReportInvocationCompletedAsync(running.InvocationId);
        await runningLease.DisposeAsync();
        await resume.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(parked.InvocationId, AssertEx.NotNull(harness.Dispatcher.SlotHolder).InvocationId);
        AssertEx.False(harness.Dispatcher.ReportInvocationAssignedAsync(Package()).IsCompleted, "the resumed turn holds the one slot");
        var notice = AssertEx.NotNull(notices.SingleOrDefault(static payload => payload.Kind == TurnNoticeKind.ResumeQueuedBehindTurn),
            "a resume that had to wait says so once");
        AssertEx.Equal(parked.InvocationId, notice.InvocationId);
        AssertEx.Equal(running.ConversationId.ToString(), notice.Detail);
    }

    [Test]
    public async Task ResumeAsync_WhenTheSlotIsFree_RetakesItAtOnce_WithNoNotice()
    {
        var harness = new Harness();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        harness.Dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var parked = Package();
        await using var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        var park = harness.Park(parked);
        park.Park();

        var resume = park.ResumeAsync(CancellationToken.None);

        AssertEx.True(resume.IsCompleted, "nothing holds the slot, so the resume does not wait");
        await resume;
        AssertEx.Equal(parked.InvocationId, AssertEx.NotNull(harness.Dispatcher.SlotHolder).InvocationId);
        AssertEx.Empty(notices);
    }

    [Test]
    public async Task AnExpiryWhileAnotherTurnHoldsTheSlot_FailsTheParkedTurnWithoutTakingOrFreeingTheSlot()
    {
        var harness = new Harness();
        var parked = Package();
        var running = Package();
        var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        var park = harness.Park(parked);
        park.Park();
        await using var runningLease = await InvocationSlotAdmission.AcquireAsync(harness.Dispatcher, running, CancellationToken.None);

        // The expiry path: the park ends without a resume, the turn reports its terminal and its lease is disposed.
        park.End();
        await harness.Dispatcher.ReportInvocationFailedAsync(parked.InvocationId, "The approval expired.", FailureCategory.Timeout);
        await parkedLease.DisposeAsync();

        AssertEx.Equal(running.InvocationId, AssertEx.NotNull(harness.Dispatcher.SlotHolder).InvocationId);
        AssertEx.False(harness.Dispatcher.ReportInvocationAssignedAsync(Package()).IsCompleted, "the expired turn's lease must not free the running turn's slot");
    }

    [Test]
    public async Task ACancelWhileParked_EndsTheTurnWithoutTakingOrFreeingTheSlot()
    {
        var harness = new Harness();
        var parked = Package();
        var running = Package();
        var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        harness.Park(parked).Park();
        await using var runningLease = await InvocationSlotAdmission.AcquireAsync(harness.Dispatcher, running, CancellationToken.None);

        harness.Tracker.Cancel(parked.InvocationId);
        await harness.Dispatcher.ReportInvocationFailedAsync(parked.InvocationId, "Stopped by user.", FailureCategory.Cancelled);
        await parkedLease.DisposeAsync();

        AssertEx.Equal(running.InvocationId, AssertEx.NotNull(harness.Dispatcher.SlotHolder).InvocationId);
        AssertEx.Empty(harness.Dispatcher.GetLiveInvocations().Where(state => state.InvocationId == parked.InvocationId));
        AssertEx.False(harness.Dispatcher.ReportInvocationAssignedAsync(Package()).IsCompleted, "the cancelled turn's lease must not free the running turn's slot");
    }

    [Test]
    public async Task ACancelDuringTheResumeWait_AbortsTheWait_AndLeavesExactlyOnePermit()
    {
        var harness = new Harness();
        var parked = Package();
        var running = Package();
        var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        var park = harness.Park(parked);
        park.Park();
        var runningLease = await InvocationSlotAdmission.AcquireAsync(harness.Dispatcher, running, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var resume = park.ResumeAsync(cancellation.Token);
        await cancellation.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => resume);
        park.End();
        await parkedLease.DisposeAsync();
        await runningLease.DisposeAsync();

        var next = harness.Dispatcher.ReportInvocationAssignedAsync(Package());
        AssertEx.True(next.IsCompleted, "the aborted wait took no permit, so the slot is free once the running turn ends");
        await using var nextLease = await next;
        AssertEx.False(harness.Dispatcher.ReportInvocationAssignedAsync(Package()).IsCompleted, "and there is still only one permit");
    }

    [Test]
    public async Task AcquireAsync_CancelledWhileQueued_TakesNoSlot()
    {
        var harness = new Harness();
        var running = Package();
        var queued = Package();
        var runningLease = await harness.Dispatcher.ReportInvocationAssignedAsync(running);
        using var cancellation = new CancellationTokenSource();

        var acquire = InvocationSlotAdmission.AcquireAsync(harness.Dispatcher, queued, cancellation.Token);
        await cancellation.CancelAsync();
        _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => acquire);

        await runningLease.DisposeAsync();
        var next = harness.Dispatcher.ReportInvocationAssignedAsync(queued);
        AssertEx.True(next.IsCompleted, "the cancelled wait must not have consumed the slot");
        await (await next).DisposeAsync();
    }

    [Test]
    public async Task APark_WhenAStateSubscriberThrows_StillGivesTheSlotBack()
    {
        var harness = new Harness();
        var parked = Package();
        await using var parkedLease = await harness.Dispatcher.ReportInvocationAssignedAsync(parked);
        harness.Dispatcher.InvocationStateChanged += (_, _) => throw new InvalidOperationException("subscriber failed");

        // The permit is returned before the state is published, so the throwing subscriber cannot keep the slot.
        _ = AssertEx.Throws<InvalidOperationException>(() => harness.Park(parked).Park());

        var next = harness.Dispatcher.ReportInvocationAssignedAsync(Package());
        AssertEx.True(next.IsCompleted, "the slot was freed despite the throwing subscriber");
    }

    private static RuntimePackage Package()
    {
        return RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
    }

    private sealed class Harness
    {
        public Harness()
        {
            Dispatcher = new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
                Substitute.For<IInvocationHistory>(),
                NullLogger<WorkerEventDispatcher>.Instance,
                TimeProvider.System);
            Tracker = new InvocationLifecycleTracker(Substitute.For<IInvocationAttachmentTracker>(),
                new PendingToolCallRegistry(),
                StubNodeRuntimeSettings.Create().Build(),
                TimeProvider.System);
        }

        public WorkerEventDispatcher Dispatcher { get; }

        public InvocationLifecycleTracker Tracker { get; }

        // The tracker entry the runner would have registered, so the park's deadline calls act on a real turn.
        public HumanPark Park(RuntimePackage package)
        {
            if (!Tracker.IsActive(package.InvocationId))
            {
                Tracker.RegisterActiveInvocation(package.InvocationId, TimeSpan.FromMinutes(5), CancellationToken.None);
            }

            return new HumanPark(Tracker, Dispatcher, package.InvocationId);
        }
    }
}
