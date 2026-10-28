namespace XE_Local_AI_Engine.Tests.Chat;

using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Chat.NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceTests
{
    [Test]
    public async Task SendMessageAsync_WhenInvocationReportsUsage_StreamsTerminalTokenCounts()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var terminalRequest = default(NodeChatTerminalizeMessageRequest);
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCompleted);
        AssertEx.Equal(expected: 10, completed.InputTokens);
        AssertEx.Equal(expected: 3, completed.OutputTokens);
        AssertEx.Equal(expected: 13, completed.TotalTokens);
        AssertEx.Equal(expected: 1, completed.ReasoningTokens);
        AssertEx.Equal(expected: 10, terminalRequest!.InputCount);
        AssertEx.Equal(expected: 13, terminalRequest.TotalCount);
    }

    [Test]
    public async Task SendMessageAsync_WhenRowTerminalAfterLease_ReportsTerminalAndClearsResume()
    {
        // A cancel that finalizes the row between the lease grant and the streaming mark: the Assigned state the lease
        // published must still reach a terminal, or a reload keeps resuming into a stream that never ends.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, static _ => { });
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(CreateAssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Cancelled, string.Empty, reasoning: null));
        var dispatcher = new RecordingWorkerEventDispatcher();
        var resumeRegistry = new InvocationResumeRegistry(dispatcher, TimeProvider.System, NullLogger<InvocationResumeRegistry>.Instance);
        var runner = new CompletingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        AssertEx.False(events.Exists(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantStreaming), "the model never ran.");
        AssertEx.Equal(InvocationStatus.Cancelled, AssertEx.NotNull(dispatcher.CurrentInvocation).Status, "the lease's Assigned state reached a terminal.");
        AssertEx.Null(resumeRegistry.TryGetLiveInvocation(requestId), "nothing is left to resume into.");
        AssertEx.Null(resumeRegistry.TryGetLiveInvocationIdForConversation(conversationId));
    }

    [Test]
    public async Task SendMessageAsync_WhileAnotherConversationsTurnIsParked_CompletesAtOnce_AndTheParkedTurnFinishesAfterIt()
    {
        var parkedConversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var dispatcher = CreateRealDispatcher();
        var runner = new ParkingInvocationRunner(dispatcher, parkedConversationId);
        var terminalOrder = new ConcurrentQueue<Guid>();
        var parkedRequest = new NodeChatStreamRequest(parkedConversationId, "use the tool", MessageId: Guid.NewGuid(), RequestId: Guid.NewGuid());
        var otherRequest = new NodeChatStreamRequest(otherConversationId, "hello", MessageId: Guid.NewGuid(), RequestId: Guid.NewGuid());
        var parkedService = CreateParkingService(CreatePersistence(parkedConversationId, parkedRequest.MessageId!.Value, parkedRequest.RequestId!.Value,
            _ => terminalOrder.Enqueue(parkedConversationId)), runner, dispatcher);
        var otherService = CreateParkingService(CreatePersistence(otherConversationId, otherRequest.MessageId!.Value, otherRequest.RequestId!.Value,
            _ => terminalOrder.Enqueue(otherConversationId)), runner, dispatcher);

        var parkedSend = DrainAsync(parkedService.SendMessageAsync(parkedRequest));
        await runner.Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var otherEvents = await DrainAsync(otherService.SendMessageAsync(otherRequest)).WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.ContainsSingle(otherEvents, static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCompleted);
        AssertEx.False(otherEvents.Exists(static streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.QueuedBehindParkedTurn)), "no parked-turn notice");
        AssertEx.False(parkedSend.IsCompleted, "the parked turn is still waiting on its card");

        runner.Answer.SetResult();
        var parkedEvents = await parkedSend.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.ContainsSingle(parkedEvents, static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCompleted);
        await AssertEx.EventuallyAsync(() => terminalOrder.Count == 2, TimeSpan.FromSeconds(10));
        AssertEx.Equal(otherConversationId, terminalOrder.First(), "the other conversation's reply finished first");
        AssertEx.Equal(parkedConversationId, terminalOrder.Last(), "the parked turn's terminal still persists after the other one's");
        AssertEx.Null(dispatcher.SlotHolder, "both leases gave the slot back");
    }

    [Test]
    public async Task SendMessageAsync_WhenAParkedTurnIsAnsweredWhileAnotherHoldsTheSlot_PersistsTheResumeQueuedNotice()
    {
        var parkedConversationId = Guid.NewGuid();
        var otherConversationId = Guid.NewGuid();
        var dispatcher = CreateRealDispatcher();
        var runner = new ParkingInvocationRunner(dispatcher, parkedConversationId, holdOthers: true);
        var resumeQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.TurnNoticeChanged += (_, args) =>
        {
            if (args.Payload.Kind == TurnNoticeKind.ResumeQueuedBehindTurn)
            {
                resumeQueued.TrySetResult();
            }
        };
        NodeChatTerminalizeMessageRequest? parkedTerminal = null;
        var parkedRequest = new NodeChatStreamRequest(parkedConversationId, "use the tool", MessageId: Guid.NewGuid(), RequestId: Guid.NewGuid());
        var otherRequest = new NodeChatStreamRequest(otherConversationId, "a long answer", MessageId: Guid.NewGuid(), RequestId: Guid.NewGuid());
        var parkedService = CreateParkingService(CreatePersistence(parkedConversationId, parkedRequest.MessageId!.Value, parkedRequest.RequestId!.Value,
            request => parkedTerminal = request), runner, dispatcher);
        var otherService = CreateParkingService(CreatePersistence(otherConversationId, otherRequest.MessageId!.Value, otherRequest.RequestId!.Value,
            static _ => { }), runner, dispatcher);

        var parkedSend = DrainAsync(parkedService.SendMessageAsync(parkedRequest));
        await runner.Parked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var otherSend = DrainAsync(otherService.SendMessageAsync(otherRequest));
        await runner.OtherRunning.Task.WaitAsync(TimeSpan.FromSeconds(10));

        runner.Answer.SetResult();
        await resumeQueued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        runner.ReleaseOther.SetResult();
        _ = await otherSend.WaitAsync(TimeSpan.FromSeconds(10));
        var parkedEvents = await parkedSend.WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.ContainsSingle(parkedEvents, static streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.ResumeQueuedBehindTurn));
        var notice = AssertEx.NotNull(AssertEx.NotNull(parkedTerminal).Parts)
                             .Single(static part => part.Kind == NodeChatMessagePartKinds.Notice && part.Name == nameof(TurnNoticeKind.ResumeQueuedBehindTurn));
        AssertEx.Equal(otherConversationId.ToString(), notice.State, "the persisted notice names the conversation that held the slot");
    }

    [Test]
    public async Task SendMessageAsync_IntoAConversationWhoseTurnIsParked_IsRefusedAsAParkedTurnConflict()
    {
        var conversationId = Guid.NewGuid();
        var dispatcher = CreateRealDispatcher();
        var parked = RuntimePackageBuilderFor(conversationId);
        await using var parkedLease = await dispatcher.ReportInvocationAssignedAsync(parked);
        await dispatcher.ReportApprovalRequestedAsync(new ApprovalRequestPayload
        {
            InvocationId = parked.InvocationId,
            RequestId = "approval-1",
            Description = "A tool call requires approval."
        });
        AssertEx.True(dispatcher.ReleaseSlotForPark(parked.InvocationId));
        var request = new NodeChatStreamRequest(conversationId, "hello again", MessageId: Guid.NewGuid(), RequestId: Guid.NewGuid());
        var persistence = CreatePersistence(conversationId, request.MessageId!.Value, request.RequestId!.Value, static _ => { });
        var service = CreateParkingService(persistence, new ParkingInvocationRunner(dispatcher, Guid.Empty), dispatcher);

        var exception = await AssertEx.ThrowsAsync<NodeChatTurnParkedException>(() => DrainAsync(service.SendMessageAsync(request)));

        AssertEx.Equal(conversationId, exception.ConversationId);
        await persistence.DidNotReceive().PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessageAsync_EmitsQueuedBeforeStreaming()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        var queuedIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantQueued);
        var streamingIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantStreaming);

        AssertEx.True(queuedIndex >= 0, "Expected an assistant-queued event.");
        AssertEx.True(streamingIndex >= 0, "Expected an assistant-streaming event.");
        AssertEx.True(queuedIndex < streamingIndex, "assistant-queued must precede assistant-streaming.");
        AssertEx.Equal(NodeChatMessageStatusValues.Queued, events[queuedIndex].Status);

        // Sequence numbers stay monotonic across the queued (front-door) and streaming (post-lease) producers.
        AssertEx.True(events[queuedIndex].Sequence < events[streamingIndex].Sequence, "Sequence must be monotonic.");
    }

    [Test]
    [Arguments("work-session", false, 1)]
    [Arguments("chat", false, 0)]
    [Arguments("work-session", true, 0)]
    public async Task SendMessageAsync_WhenAnotherConversationHoldsTheSlot_NamesAWorkSessionHolderOnly(string holderKind, bool holderParked, int expectedNotices)
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var holderConversationId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        persistence.GetConversationKindAsync(holderConversationId, Arg.Any<CancellationToken>()).Returns(holderKind);
        var dispatcher = new RecordingWorkerEventDispatcher();
        // The holder's turn is live when this send arrives: the fake hands the slot over at once, so only the notice can tell.
        await using var holderLease = await dispatcher.ReportInvocationAssignedAsync(RuntimePackageBuilder.Valid().WithConversationId(holderConversationId).Build());
        // A parked step gives the slot back, so it is not the holder and nothing queues behind it.
        if (holderParked)
        {
            AssertEx.NotNull(dispatcher.CurrentInvocation).PendingApproval = ParkedApproval();
            AssertEx.True(dispatcher.ReleaseSlotForPark(AssertEx.NotNull(dispatcher.CurrentInvocation).InvocationId));
        }

        var runner = new CompletingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        var notices = events.Where(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                  && streamEvent.NoticeKind == nameof(TurnNoticeKind.QueuedBehindWorkSession))
                            .ToList();
        AssertEx.Equal(expectedNotices, notices.Count);
        AssertEx.True(notices.TrueForAll(notice => notice.NoticeDetail == holderConversationId.ToString()), "The notice names the conversation that holds the slot.");
    }

    [Test]
    public async Task SendMessageAsync_WhenToolLifecycleReported_StreamsToolCallEvents()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ToolEmittingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)))
        {
            events.Add(streamEvent);
        }

        var requested = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested);
        AssertEx.Equal("call-1", requested.ToolCallId);
        AssertEx.Equal("weather", requested.ToolName);
        AssertEx.Equal("{\"city\":\"berlin\"}", requested.Arguments);
        AssertEx.Equal(expected: false, requested.RequiresApproval);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, requested.Status);

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallCompleted);
        AssertEx.Equal("call-1", completed.ToolCallId);
        AssertEx.Equal("weather", completed.ToolName);
        AssertEx.Equal("sunny", completed.Result);
        AssertEx.Equal(expected: false, completed.IsError);

        var requestedIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested);
        var completedIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallCompleted);
        AssertEx.True(requestedIndex < completedIndex, "tool-call-requested must precede tool-call-completed.");
        AssertEx.True(events[requestedIndex].Sequence < events[completedIndex].Sequence, "Sequence must be monotonic.");
    }

    [Test]
    public async Task SendMessageAsync_ThreadsReasoningEffortIntoRuntimePackage()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           ReasoningEffort: "low")))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal("low", runner.LastReasoningEffort);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task SendMessageAsync_WhenTheTurnIsAWorkSessionStep_NeverAllowsAnAutoModelSwap(bool isWorkSessionTurn, bool expectedAllowAutoModelSwap)
    {
        // The exact shape this closes: a development-workflow node that authors NEITHER a model nor an effort, on
        // an agent that pins neither, with no tools — swap-eligible on every other signal. The work-session supervisor
        // sets IsWorkSessionTurn on every step whatever the node authored, so the graph's step is never served by a
        // model its author did not choose. The same send WITHOUT the flag is an ordinary chat turn and stays eligible.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           IsWorkSessionTurn: isWorkSessionTurn)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the send to reach the runner.");
        AssertEx.Equal(expectedAllowAutoModelSwap, runner.LastAllowAutoModelSwap);
    }

    [Test]
    public async Task SendMessageAsync_WhenReasoningEffortOmitted_LeavesRuntimePackageReasoningNull()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the invocation runner to observe the package.");
        AssertEx.True(runner.LastReasoningEffort is null, "Expected reasoning effort to default to null.");
    }

    [Test]
    public async Task SendMessageAsync_ThreadsSamplingOptionsIntoRuntimePackage()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var sampling = new SamplingOptions
        {
            Temperature = 0.4f,
            TopP = 0.9f,
            NumCtx = 8192
        };

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           SamplingOptions: sampling)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var captured = AssertEx.NotNull(runner.LastSamplingOptions);
        AssertEx.Equal(expected: 0.4f, captured.Temperature);
        AssertEx.Equal(expected: 0.9f, captured.TopP);
        AssertEx.Equal(expected: 8192, captured.NumCtx);
    }

    [Test]
    public async Task SendMessageAsync_ThreadsTheWebReviewModeIntoRuntimePackage()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        _ = await service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                             "hello",
                             MessageId: assistantMessageId,
                             RequestId: requestId,
                             AutoAcceptWebContent: true))
                         .ToListAsync();

        AssertEx.True(runner.LastAutoAcceptWebContent, "the conversation's auto mode must reach the runtime package");
    }

    [Test]
    public async Task SendMessageAsync_WhenSamplingOptionsOmitted_LeavesRuntimePackageSamplingNull()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the invocation runner to observe the package.");
        AssertEx.True(runner.LastSamplingOptions is null, "Expected sampling options to default to null.");
    }

    [Test]
    public async Task SendMessageAsync_WhenUserCancelsThroughRegistry_TerminalizesAssistantAsCancelled()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var terminalRequest = default(NodeChatTerminalizeMessageRequest);
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new StreamingUntilCancelledInvocationRunner(dispatcher);
        var cancellationRegistry = new NodeChatStreamCancellationRegistry();
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            cancellationRegistry,
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        // The stop button is a SEPARATE request that routes through the cancellation registry (the real cancel
        // path), NOT the client connection token. Trigger it mid-stream and assert the runner-driven Cancelled
        // terminal is persisted.
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantDelta)
            {
                AssertEx.True(cancellationRegistry.TryCancel(new NodeChatMessageCorrelation
                    {
                        ConversationId = conversationId,
                        MessageId = assistantMessageId,
                        RequestId = requestId
                    }),
                    "Expected the active stream to be registered for cancellation.");
            }
        }

        await AssertEx.EventuallyAsync(() => terminalRequest is not null, TimeSpan.FromSeconds(5));
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, terminalRequest!.Status);
        AssertEx.Equal(conversationId, terminalRequest.Correlation.ConversationId);
        AssertEx.Equal(assistantMessageId, terminalRequest.Correlation.MessageId);
        AssertEx.Equal(requestId, terminalRequest.Correlation.RequestId);
        AssertEx.Equal("thinking", terminalRequest.Reasoning);
    }

    [Test]
    public async Task SendMessageAsync_WhenQueuedMarkRejectedByCancel_EmitsTerminalAndDoesNotRun()
    {
        // The queued mark is rejected because a cancel already finalized the row (the reported cancel-before-queued race).
        // The stream must surface the terminal the row holds and abort BEFORE wiring the runner — never stream into it.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        persistence.MarkAssistantQueuedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(CreateAssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Cancelled, string.Empty, reasoning: null));
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = Substitute.For<IInvocationRunner>();
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId, "hello", MessageId: assistantMessageId, RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        AssertEx.Contains(events, streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCancelled);
        AssertEx.False(events.Any(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantQueued), "a rejected queued mark must not emit AssistantQueued");
        AssertEx.False(events.Any(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantStreaming), "an aborted turn must never reach streaming");
        await runner.DidNotReceive().RunAsync(Arg.Any<InvocationExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessageAsync_WhenClientDisconnectsBeforeRunnerCompletes_PersistsCompletedNotInterrupted()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var terminalRequest = default(NodeChatTerminalizeMessageRequest);
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();

        // The runner publishes a delta, then BLOCKS until the test signals — simulating the runner still working
        // when the client disconnects. Once released it reports the real Completed terminal.
        var releaseRunner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GatedCompletingInvocationRunner(dispatcher, releaseRunner.Task);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
        using var clientCancellation = new CancellationTokenSource();
        var events = new List<ChatStreamEvent>();
        var clientDisconnected = false;

        try
        {
            await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                                   "hello",
                                   MessageId: assistantMessageId,
                                   RequestId: requestId),
                               clientCancellation.Token))
            {
                events.Add(streamEvent);

                // Simulate a SignalR/SSE client disconnect mid-stream: cancel the client token (which cancels the
                // enumeration) WHILE the runner is still blocked, then release the runner to report Completed.
                if (streamEvent.Type == ChatStreamEventTypes.AssistantDelta && !clientDisconnected)
                {
                    clientDisconnected = true;
                    await clientCancellation.CancelAsync();
                    releaseRunner.SetResult();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling the client token aborts the SSE enumeration. Persistence must still complete.
        }

        // The async-iterator finally block awaits Task.WhenAll(runTask, pumpTask) before propagating the OCE,
        // so by the time we reach here the pump has already persisted the runner's terminal. Direct assert —
        // no polling or wall-clock timeout needed.
        AssertEx.NotNull(terminalRequest, "Expected the pump to have persisted a terminal status.");
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, terminalRequest!.Status);
        AssertEx.True(terminalRequest.Status != NodeChatMessageStatusValues.Interrupted, "Client disconnect must not force interrupted.");
    }

    [Test]
    public async Task SendMessageAsync_WhenClientDisconnectsBeforeRunOwnership_TerminalizesAssistantAsInterrupted()
    {
        // A disconnect AFTER the assistant row is persisted (Pending/Queued) but BEFORE the pump + runner take
        // ownership must terminalize the row (Interrupted) rather than leave it dangling until the restart reaper.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var terminalRequest = default(NodeChatTerminalizeMessageRequest);
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);

        // Block the pre-ownership GetEnableToolsAsync until the client disconnects, so the teardown lands squarely in the
        // pre-ownership window (before the run/pump tasks are created).
        var enableToolsGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetEnableToolsAsync(Arg.Any<CancellationToken>()).Returns(enableToolsGate.Task);

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            runtimeSettings,
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());
        using var clientCancellation = new CancellationTokenSource();
        // The blocked GetEnableToolsAsync is released as a cancellation when the client disconnects.
        await using var gateRegistration = clientCancellation.Token.Register(() => enableToolsGate.TrySetCanceled());
        var reachedQueued = false;

        try
        {
            await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                                   "hello",
                                   MessageId: assistantMessageId,
                                   RequestId: requestId),
                               clientCancellation.Token))
            {
                if (streamEvent.Type == ChatStreamEventTypes.AssistantQueued)
                {
                    // The row is now persisted Queued; the next MoveNextAsync blocks at GetEnableToolsAsync. Disconnect
                    // now so the teardown happens before run ownership is established.
                    reachedQueued = true;
                    await clientCancellation.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the client disconnected before run ownership; the enumeration aborts.
        }

        AssertEx.True(reachedQueued, "Expected the stream to reach the queued state before the disconnect.");
        AssertEx.NotNull(terminalRequest, "A disconnect before run ownership must terminalize the stranded assistant row.");
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, terminalRequest!.Status);
        AssertEx.Equal(assistantMessageId, terminalRequest.Correlation.MessageId);
        AssertEx.Equal(requestId, terminalRequest.Correlation.RequestId);
        // The pre-ownership teardown must carry a run envelope so the terminal row gets its durable envelope in the same
        // transaction — otherwise this is the one live path writing a terminal without an atomic envelope.
        AssertEx.NotNull(terminalRequest.Envelope, "A pre-ownership interrupted terminalize must carry a run envelope.");
    }

    [Test]
    public async Task SendMessage_AppliesOperatorMaxMessageRequestTimeoutToRuntimePackage()
    {
        // The operator raised the node-level "Maximum message request timeout" to 900s. The send path must thread it
        // onto the runtime package as the invocation timeout — a null timeout block would let the builder's own
        // default cut long turns off with FailureCategory=Timeout. 900 is deliberately NOT the
        // default, so this still fails if the wiring is dropped. Tool-call/stream-idle are not operator-controlled.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(maxMessageRequestTimeoutSeconds: 900),
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            new PermissiveToolApprovalPolicy(),
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var streamed = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            streamed.Add(streamEvent);
        }

        AssertEx.True(streamed.Count > 0, "Expected the send to stream events.");
        AssertEx.NotNull(runner.LastTimeouts);
        AssertEx.Equal(expected: 900, runner.LastTimeouts!.InvocationTimeoutSeconds);
        AssertEx.Equal(expected: 30, runner.LastTimeouts.ToolCallTimeoutSeconds);
        AssertEx.Equal(expected: 60, runner.LastTimeouts.StreamIdleTimeoutSeconds);

        // The same ceiling rides the queued + streaming events so the browser's own stream watchdog can derive its
        // deadline from the node's ceiling instead of a fixed constant that would pre-empt it. Queued carries it too
        // (not just streaming) because the collision-queue wait ahead of the streaming transition is the first stretch
        // of the turn where nothing at all reaches the client.
        AssertEx.ContainsSingle(streamed,
            streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantQueued && streamEvent.InvocationTimeoutSeconds == 900);
        AssertEx.ContainsSingle(streamed,
            streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantStreaming && streamEvent.InvocationTimeoutSeconds == 900);
        // Every other event type keeps a null ceiling — the field is a targeted hint, not a per-frame tax.
        AssertEx.True(streamed.TrueForAll(streamEvent => streamEvent.InvocationTimeoutSeconds is null
                                                         || streamEvent.Type == ChatStreamEventTypes.AssistantQueued
                                                         || streamEvent.Type == ChatStreamEventTypes.AssistantStreaming),
            "Expected only the queued/streaming events to carry the invocation timeout.");
    }

    // A pump that hits a disposed service while the host stops (a staged restore quits the node) is a shutdown, not a fault.
    [Test]
    public async Task SendMessageAsync_WhenPumpHitsADisposedServiceWhileTheHostStops_LogsNoError()
    {
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        lifetime.StopApplication();
        var logger = new RecordingLogger<NodeChatStreamService>();

        await DrainWithDisposedPumpAsync(lifetime, logger);

        AssertEx.Equal(0, logger.CountContaining(PumpFaultedFragment, LogLevel.Error), "A disposal during host stop is not an Error.");
        AssertEx.Equal(1, logger.CountContaining("pump stopped with the host", LogLevel.Debug), "The disposal is still recorded at Debug.");
    }

    [Test]
    public async Task SendMessageAsync_WhenPumpHitsADisposedServiceWhileTheHostRuns_LogsTheFault()
    {
        // The control for the test above: the same disposal on a running host is a real fault and stays loud.
        var lifetime = new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance);
        var logger = new RecordingLogger<NodeChatStreamService>();

        await DrainWithDisposedPumpAsync(lifetime, logger);

        AssertEx.Equal(1, logger.CountContaining(PumpFaultedFragment, LogLevel.Error), "A disposal on a running host is an Error.");
    }
}
