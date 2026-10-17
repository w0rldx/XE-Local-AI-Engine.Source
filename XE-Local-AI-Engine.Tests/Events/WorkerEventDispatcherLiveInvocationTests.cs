namespace XE_Local_AI_Engine.Tests.Events;

using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Contracts.Events;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Two live invocations on the real <see cref="WorkerEventDispatcher" />: the first keeps receiving its updates and its terminal
///     after a second one is assigned, and an approval or answer reaches only the turn whose request id it carries.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WorkerEventDispatcherLiveInvocationTests
{
    [Test]
    public async Task AnUpdateToALiveTurnThatIsNotTheLatest_IsApplied_AndItsTerminalIsPublished()
    {
        var dispatcher = CreateDispatcher();
        var published = new List<InvocationState>();
        dispatcher.InvocationStateChanged += (_, args) => published.Add(args.State);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var firstLease = await AssignAsync(dispatcher, first);
        ReleaseSlotAsIfParked(dispatcher);
        await using var secondLease = await AssignAsync(dispatcher, second);

        await dispatcher.ReportInvocationStreamChunkAsync(first, "hello");

        AssertEx.Equal(second, AssertEx.NotNull(dispatcher.CurrentInvocation).InvocationId);
        AssertEx.Equal("hello", dispatcher.GetLiveInvocations().Single(state => state.InvocationId == first).StreamedContent);

        await dispatcher.ReportInvocationCompletedAsync(first);

        AssertEx.ContainsSingle(published, state => state.InvocationId == first && state.Status == InvocationStatus.Completed);
        AssertEx.Equal(second, dispatcher.GetLiveInvocations().Single().InvocationId);
        AssertEx.Equal(second, AssertEx.NotNull(dispatcher.SlotHolder).InvocationId);

        // The first lease no longer holds the slot, so disposing it must not free the second turn's slot.
        await firstLease.DisposeAsync();
        AssertEx.Equal(second, AssertEx.NotNull(dispatcher.SlotHolder).InvocationId);
        AssertEx.Equal(expected: 0, SlotQueue(dispatcher).CurrentCount);
    }

    [Test]
    public async Task SlotHolder_IsNullOnceTheHoldingLeaseIsDisposed_AndTheTurnLeavesTheLiveList()
    {
        var dispatcher = CreateDispatcher();
        var invocationId = Guid.NewGuid();

        var lease = await AssignAsync(dispatcher, invocationId);
        AssertEx.Equal(invocationId, AssertEx.NotNull(dispatcher.SlotHolder).InvocationId);

        await lease.DisposeAsync();

        AssertEx.Null(dispatcher.SlotHolder);
        AssertEx.Empty(dispatcher.GetLiveInvocations());
        AssertEx.Equal(invocationId, AssertEx.NotNull(dispatcher.CurrentInvocation).InvocationId);
    }

    [Test]
    public async Task ApprovalResolution_ClearsOnlyTheMatchingTurnsPendingApproval()
    {
        var dispatcher = CreateDispatcher();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var firstLease = await AssignAsync(dispatcher, first);
        await RequestApprovalAsync(dispatcher, first, "approval-first");
        ReleaseSlotAsIfParked(dispatcher);
        await using var secondLease = await AssignAsync(dispatcher, second);
        await RequestApprovalAsync(dispatcher, second, "approval-second");

        await dispatcher.DispatchApprovalResolvedAsync(new ApprovalResolvedEvent
        {
            RequestId = "approval-first",
            Approved = true
        });

        var live = dispatcher.GetLiveInvocations();
        var firstState = live.Single(state => state.InvocationId == first);
        var secondState = live.Single(state => state.InvocationId == second);
        AssertEx.Null(firstState.PendingApproval);
        AssertEx.Equal("approval-first", AssertEx.NotNull(firstState.LastApprovalResolution).RequestId);
        AssertEx.Equal("approval-second", AssertEx.NotNull(secondState.PendingApproval).RequestId);
        AssertEx.Null(secondState.LastApprovalResolution);
    }

    [Test]
    public async Task QuestionAnswer_ClearsOnlyTheMatchingTurnsPendingQuestion()
    {
        var dispatcher = CreateDispatcher();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using var firstLease = await AssignAsync(dispatcher, first);
        await AskAsync(dispatcher, first, "question-first");
        ReleaseSlotAsIfParked(dispatcher);
        await using var secondLease = await AssignAsync(dispatcher, second);
        await AskAsync(dispatcher, second, "question-second");

        await dispatcher.DispatchUserQuestionAnsweredAsync(new UserQuestionAnsweredEvent
        {
            RequestId = "question-first",
            Answers = []
        });

        var live = dispatcher.GetLiveInvocations();
        AssertEx.Null(live.Single(state => state.InvocationId == first).PendingQuestion);
        AssertEx.Equal("question-second", AssertEx.NotNull(live.Single(state => state.InvocationId == second).PendingQuestion).RequestId);
    }

    [Test]
    public async Task ReportInvocationAssignedAsync_ForAnIdThatIsAlreadyLive_ThrowsAndReturnsThePermit()
    {
        var dispatcher = CreateDispatcher();
        var invocationId = Guid.NewGuid();
        await using var lease = await AssignAsync(dispatcher, invocationId);
        ReleaseSlotAsIfParked(dispatcher);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => AssignAsync(dispatcher, invocationId));

        AssertEx.Equal(expected: 1, SlotQueue(dispatcher).CurrentCount, "the refused assignment must hand its permit back");
        AssertEx.Null(dispatcher.SlotHolder);
    }

    [Test]
    public async Task GetLiveSnapshot_PrefersTheSlotHolder_AndNeverLabelsAParkedTurnCurrent()
    {
        var dispatcher = CreateDispatcher();
        var parked = Guid.NewGuid();
        var running = Guid.NewGuid();
        await using var parkedLease = await AssignAsync(dispatcher, parked);
        ReleaseSlotAsIfParked(dispatcher);

        var whileOnlyParked = dispatcher.GetLiveSnapshot();
        AssertEx.Null(whileOnlyParked.Current, "a live parked turn appears in Live, not as Current");
        AssertEx.Equal(parked, whileOnlyParked.Live.Single().InvocationId);

        var runningLease = await AssignAsync(dispatcher, running);
        AssertEx.Equal(running, AssertEx.NotNull(dispatcher.GetLiveSnapshot().Current).InvocationId);
        AssertEx.Equal(expected: 2, dispatcher.GetLiveSnapshot().Live.Count);

        await dispatcher.ReportInvocationCompletedAsync(running);
        await runningLease.DisposeAsync();
        await dispatcher.ReportInvocationCompletedAsync(parked);

        var idle = dispatcher.GetLiveSnapshot();
        AssertEx.Equal(running, AssertEx.NotNull(idle.Current).InvocationId, "with nothing live, Current is the last assigned turn");
        AssertEx.Empty(idle.Live);
    }

    private static WorkerEventDispatcher CreateDispatcher()
    {
        return new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
            Substitute.For<IInvocationHistory>(),
            NullLogger<WorkerEventDispatcher>.Instance,
            TimeProvider.System);
    }

    private static Task<IAsyncDisposable> AssignAsync(WorkerEventDispatcher dispatcher, Guid invocationId)
    {
        var package = RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithConversationId(Guid.NewGuid()).Build();
        return dispatcher.ReportInvocationAssignedAsync(package);
    }

    private static Task RequestApprovalAsync(WorkerEventDispatcher dispatcher, Guid invocationId, string requestId)
    {
        return dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
        {
            InvocationId = invocationId,
            RequestId = requestId,
            Description = "A tool call requires approval."
        });
    }

    private static Task AskAsync(WorkerEventDispatcher dispatcher, Guid invocationId, string requestId)
    {
        return dispatcher.ReportUserQuestionAsync(new UserQuestionLifecyclePayload
        {
            InvocationId = invocationId,
            RequestId = requestId,
            CallId = $"call-{requestId}",
            ToolName = "ask_user",
            Questions = []
        });
    }

    // Frees the slot the way a human park will, so a second turn can be assigned while the first stays live. No public member does
    // this yet; the holder is cleared and the permit returned exactly as the holding lease's dispose would.
    private static void ReleaseSlotAsIfParked(WorkerEventDispatcher dispatcher)
    {
        var holder = AssertEx.NotNull(typeof(WorkerEventDispatcher).GetField("_slotHolderId", BindingFlags.Instance | BindingFlags.NonPublic));
        holder.SetValue(dispatcher, null);
        _ = SlotQueue(dispatcher).Release();
    }

    private static SemaphoreSlim SlotQueue(WorkerEventDispatcher dispatcher)
    {
        var field = AssertEx.NotNull(typeof(WorkerEventDispatcher).GetField("_invocationQueue", BindingFlags.Instance | BindingFlags.NonPublic));
        return (SemaphoreSlim)AssertEx.NotNull(field.GetValue(dispatcher));
    }
}
