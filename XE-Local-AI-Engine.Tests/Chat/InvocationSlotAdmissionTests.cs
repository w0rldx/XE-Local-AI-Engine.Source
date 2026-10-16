namespace XE_Local_AI_Engine.Tests.Chat;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     R4-03: a turn parked on an unanswered approval keeps the node's one invocation slot, so a send in another
///     conversation waited with no explanation. Driven through the real <see cref="WorkerEventDispatcher" /> slot.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationSlotAdmissionTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AcquireAsync_QueuedBehindAParkedTurnInAnotherConversation_NoticesOnce_AndCompletesOnceTheCardIsAnswered(bool parkedBeforeQueueing)
    {
        var dispatcher = CreateDispatcher();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var parked = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
        var queued = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();

        var parkedLease = await dispatcher.ReportInvocationAssignedAsync(parked);
        if (parkedBeforeQueueing)
        {
            await ParkAsync(dispatcher, parked.InvocationId);
        }

        var acquire = InvocationSlotAdmission.AcquireAsync(dispatcher, queued, queued.InvocationId, NullLogger.Instance, CancellationToken.None);
        if (!parkedBeforeQueueing)
        {
            await ParkAsync(dispatcher, parked.InvocationId);
        }

        AssertEx.False(acquire.IsCompleted, "the parked turn still holds the slot");
        AssertEx.ContainsSingle(notices, static payload => payload.Kind == TurnNoticeKind.QueuedBehindParkedTurn);
        var notice = notices.Single(static payload => payload.Kind == TurnNoticeKind.QueuedBehindParkedTurn);
        AssertEx.Equal(queued.InvocationId, notice.InvocationId);
        AssertEx.Equal(parked.ConversationId.ToString(), notice.Detail);

        // The card is answered: the parked turn finishes and releases the slot, and the queued turn runs.
        await parkedLease.DisposeAsync();
        await using var queuedLease = await acquire.WaitAsync(TimeSpan.FromSeconds(10));
        AssertEx.Equal(queued.InvocationId, AssertEx.NotNull(dispatcher.CurrentInvocation).InvocationId);
        AssertEx.Equal(expected: 1, notices.Count(static payload => payload.Kind == TurnNoticeKind.QueuedBehindParkedTurn), "the notice fires once per queued turn");
    }

    [Test]
    public async Task AcquireAsync_QueuedBehindAStreamingTurn_StaysSilent()
    {
        var dispatcher = CreateDispatcher();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var holder = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
        var queued = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();

        var holderLease = await dispatcher.ReportInvocationAssignedAsync(holder);
        var acquire = InvocationSlotAdmission.AcquireAsync(dispatcher, queued, queued.InvocationId, NullLogger.Instance, CancellationToken.None);
        await holderLease.DisposeAsync();
        await using var queuedLease = await acquire.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Empty(notices, "a turn that is simply generating is an ordinary queue, not a parked one");
    }

    [Test]
    public async Task AcquireAsync_CancelledWhileQueued_TakesNoSlotAndStopsListening()
    {
        var dispatcher = CreateDispatcher();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var parked = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
        var queued = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
        var parkedLease = await dispatcher.ReportInvocationAssignedAsync(parked);
        using var cancellation = new CancellationTokenSource();

        var acquire = InvocationSlotAdmission.AcquireAsync(dispatcher, queued, queued.InvocationId, NullLogger.Instance, cancellation.Token);
        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => acquire);

        // Unsubscribed: a park after the cancel raises nothing for the cancelled turn.
        await ParkAsync(dispatcher, parked.InvocationId);
        AssertEx.Empty(notices, "a cancelled turn must stop listening for the holder");

        // And no slot was taken: once the holder releases, the next turn is admitted at once.
        await parkedLease.DisposeAsync();
        var next = dispatcher.ReportInvocationAssignedAsync(queued);
        AssertEx.True(next.IsCompleted, "the cancelled wait must not have consumed the slot");
        await (await next).DisposeAsync();
    }

    [Test]
    public async Task AcquireAsync_BehindAParkedTurnOfTheSameConversation_StaysSilent()
    {
        var dispatcher = CreateDispatcher();
        var notices = new ConcurrentQueue<TurnNoticePayload>();
        dispatcher.TurnNoticeChanged += (_, args) => notices.Enqueue(args.Payload);
        var conversationId = Guid.NewGuid();
        var parked = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(conversationId).Build();
        var queued = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(conversationId).Build();
        var parkedLease = await dispatcher.ReportInvocationAssignedAsync(parked);
        await ParkAsync(dispatcher, parked.InvocationId);

        var acquire = InvocationSlotAdmission.AcquireAsync(dispatcher, queued, queued.InvocationId, NullLogger.Instance, CancellationToken.None);
        await parkedLease.DisposeAsync();
        await using var queuedLease = await acquire.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Empty(notices, "the card the turn waits on is in its own conversation, where the operator already sees it");
    }

    [Test]
    public async Task AcquireAsync_WhenANoticeSubscriberThrows_StillReturnsTheLease()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.TurnNoticeChanged += (_, _) => throw new InvalidOperationException("subscriber failed");
        var parked = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
        var queued = RuntimePackageBuilder.Valid().WithInvocationId(Guid.NewGuid()).WithConversationId(Guid.NewGuid()).Build();
        var parkedLease = await dispatcher.ReportInvocationAssignedAsync(parked);
        await ParkAsync(dispatcher, parked.InvocationId);

        var acquire = InvocationSlotAdmission.AcquireAsync(dispatcher, queued, queued.InvocationId, NullLogger.Instance, CancellationToken.None);
        AssertEx.False(acquire.IsCompleted, "the throwing notice must not end the wait");
        await parkedLease.DisposeAsync();
        await using var queuedLease = await acquire.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Equal(queued.InvocationId, AssertEx.NotNull(dispatcher.CurrentInvocation).InvocationId);
    }

    private static WorkerEventDispatcher CreateDispatcher()
    {
        return new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);
    }

    private static Task ParkAsync(WorkerEventDispatcher dispatcher, Guid invocationId)
    {
        return dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
        {
            InvocationId = invocationId,
            RequestId = "approval-1",
            Description = "A tool call requires approval."
        });
    }
}
