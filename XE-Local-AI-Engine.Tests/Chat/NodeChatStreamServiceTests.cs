namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantCompleted);
        AssertEx.Equal(10, completed.InputTokens);
        AssertEx.Equal(3, completed.OutputTokens);
        AssertEx.Equal(13, completed.TotalTokens);
        AssertEx.Equal(1, completed.ReasoningTokens);
        AssertEx.Equal(10, terminalRequest!.InputCount);
        AssertEx.Equal(13, terminalRequest.TotalCount);
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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
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
    public async Task SendMessageAsync_WhenToolLifecycleReported_StreamsToolCallEvents()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ToolEmittingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);
        var events = new List<ChatStreamEvent>();

        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        var requested = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested);
        AssertEx.Equal("call-1", requested.ToolCallId);
        AssertEx.Equal("weather", requested.ToolName);
        AssertEx.Equal("{\"city\":\"berlin\"}", requested.Arguments);
        AssertEx.Equal(false, requested.RequiresApproval);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, requested.Status);

        var completed = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallCompleted);
        AssertEx.Equal("call-1", completed.ToolCallId);
        AssertEx.Equal("weather", completed.ToolName);
        AssertEx.Equal("sunny", completed.Result);
        AssertEx.Equal(false, completed.IsError);

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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           ReasoningEffort: "low")).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal("low", runner.LastReasoningEffort);
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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

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
                           SamplingOptions: sampling)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var captured = AssertEx.NotNull(runner.LastSamplingOptions);
        AssertEx.Equal(0.4f, captured.Temperature);
        AssertEx.Equal(0.9f, captured.TopP);
        AssertEx.Equal(8192, captured.NumCtx);
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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the invocation runner to observe the package.");
        AssertEx.True(runner.LastSamplingOptions is null, "Expected sampling options to default to null.");
    }

    [Test]
    public async Task SendMessageAsync_WhenUseLocalTools_OffersCatalogToolsOnRuntimePackage()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(2, runner.LastAllowedTools.Count);
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == "GetCurrentTime");
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == "Calculate");
        foreach (var tool in runner.LastAllowedTools)
        {
            AssertEx.Equal(ToolLocation.ClientLocal, tool.Location);
            AssertEx.NotNullOrEmpty(tool.ParameterSchema);
        }
    }

    [Test]
    public async Task SendMessageAsync_WhenLocalToolsDisabled_OffersNoTools()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: false)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Empty(runner.LastAllowedTools);
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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            cancellationRegistry,
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        // The stop button is a SEPARATE request that routes through the cancellation registry (the real cancel
        // path), NOT the client connection token. Trigger it mid-stream and assert the runner-driven Cancelled
        // terminal is persisted.
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            if (streamEvent.Type == ChatStreamEventTypes.AssistantDelta)
            {
                AssertEx.True(cancellationRegistry.TryCancel(new NodeChatMessageCorrelation(conversationId, assistantMessageId, requestId)),
                    "Expected the active stream to be registered for cancellation.");
            }
        }

        await AssertEx.EventuallyAsync(() => terminalRequest is not null, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, terminalRequest!.Status);
        AssertEx.Equal(conversationId, terminalRequest.Correlation.ConversationId);
        AssertEx.Equal(assistantMessageId, terminalRequest.Correlation.MessageId);
        AssertEx.Equal(requestId, terminalRequest.Correlation.RequestId);
        AssertEx.Equal("thinking", terminalRequest.Reasoning);
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
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);
        using var clientCancellation = new CancellationTokenSource();
        var events = new List<ChatStreamEvent>();
        var clientDisconnected = false;

        try
        {
            await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                                   "hello",
                                   MessageId: assistantMessageId,
                                   RequestId: requestId),
                               clientCancellation.Token).ConfigureAwait(false))
            {
                events.Add(streamEvent);

                // Simulate a SignalR/SSE client disconnect mid-stream: cancel the client token (which cancels the
                // enumeration) WHILE the runner is still blocked, then release the runner to report Completed.
                if (streamEvent.Type == ChatStreamEventTypes.AssistantDelta && !clientDisconnected)
                {
                    clientDisconnected = true;
                    await clientCancellation.CancelAsync().ConfigureAwait(false);
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
    public async Task SendMessageAsync_WhenConversationBound_StreamsWithDefinitionPromptToolsAndVersion()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);

        var boundTool = CreateLocalToolDto("Calculate", "{\"type\":\"object\"}");
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Bound persona prompt.", [boundTool], "qwen3:8b", "high", 9));

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")),
            resolver,
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal("Bound persona prompt.", runner.LastSystemPrompt);
        AssertEx.Equal(9, runner.LastAgentDefinitionVersion);
        AssertEx.Equal("high", runner.LastReasoningEffort);
        AssertEx.Equal(1, runner.LastAllowedTools.Count);
        AssertEx.Equal("Calculate", runner.LastAllowedTools[0].Name);
        AssertEx.True(runner.LastOrchestrationSpec is null, "A single-agent binding must carry no orchestration spec.");
        // The just-sent user turn ("hello") is threaded to the resolver as the relevance-retrieval query —
        // not just any string, the actual turn content drives which playbook actions are injected.
        await resolver.Received().ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Is<string?>(query => query == "hello"), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SendMessageAsync_WhenBoundToOrchestrator_CarriesOrchestrationSpecOnPackage()
    {
        // Orchestrator hydration: a conversation bound to a Kind=Orchestrator definition whose orchestration resolver
        // returns a spec must carry that spec on the runtime package so the runner branches to the handoff workflow.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);

        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentDefinitionId, Arg.Any<CancellationToken>()).Returns(CreateOrchestratorRecord(agentDefinitionId));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        var spec = CreateSampleSpec();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(new ResolvedOrchestration(spec, "Orchestrator prompt.", "qwen3:8b", null, 4));

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            store,
            CreateDefaultAgentProvider(),
            orchestrationResolver,
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.NotNull(runner.LastOrchestrationSpec);
        AssertEx.Equal(spec.TriageParticipantKey, runner.LastOrchestrationSpec!.TriageParticipantKey);
        AssertEx.Equal(2, runner.LastOrchestrationSpec.Participants.Count);
    }

    [Test]
    public async Task SendMessageAsync_WhenConversationUnbound_StreamsWithDefaultPromptAndVersion()
    {
        // Regression guard: an unbound conversation must use today's literals — the embedded prompt and version 1 —
        // and the resolver must be consulted with a null binding (the default-persona contract).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var resolver = CreateAgentDefinitionResolver();

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            resolver,
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(1, runner.LastAgentDefinitionVersion);
        AssertEx.NotNullOrEmpty(runner.LastSystemPrompt);
        await resolver.Received().ResolveAsync(null, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SendMessage_WhenRequestAgentIdSet_ResolvesThatAgent()
    {
        // The per-send selected agent (request.AgentDefinitionId) wins over the conversation binding. The conversation
        // is bound to a DIFFERENT agent; the resolver must be consulted with the request id, not the conversation id.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var conversationAgentId = Guid.NewGuid();
        var requestAgentId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, conversationAgentId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Selected persona.", [], "qwen3:8b", null, 3, requestAgentId, "Selected Agent"));

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            resolver,
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           AgentDefinitionId: requestAgentId)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        await resolver.Received().ResolveAsync(requestAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await resolver.DidNotReceive().ResolveAsync(conversationAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SendMessage_WhenNoAgentId_FallsBackToDefaultAssistant()
    {
        // No request agent id AND no conversation binding: the effective-agent precedence falls back to the seeded
        // Default Assistant id, so the resolver is consulted with THAT id (not null).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var defaultAssistantId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Default persona.", [], null, null, 1, defaultAssistantId, "Default Assistant"));

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            resolver,
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(defaultAssistantId),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        await resolver.Received().ResolveAsync(defaultAssistantId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SendMessage_StampsAgentIdAndNameOnAssistantMessage()
    {
        // The placeholder is stamped with the resolved agent's id + display-name snapshot (per-response attribution).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        NodeChatCreateAssistantPlaceholderRequest? capturedPlaceholder = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { },
            placeholderObserver: request => capturedPlaceholder = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Persona.", [], null, null, 1, agentId, "Backend Buddy"));

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            resolver,
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(agentId),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.NotNull(capturedPlaceholder);
        AssertEx.Equal(agentId, capturedPlaceholder!.AgentDefinitionId);
        AssertEx.Equal("Backend Buddy", capturedPlaceholder.AgentName);
    }

    [Test]
    public async Task SendMessage_PendingMessageCarriesAgentName()
    {
        // Locks the §7.4 hoist: the resolve happens BEFORE the placeholder is minted, so the placeholder request (the
        // AssistantPending source) already carries the agent name — not just the terminal.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        NodeChatCreateAssistantPlaceholderRequest? capturedPlaceholder = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { },
            placeholderObserver: request => capturedPlaceholder = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Persona.", [], null, null, 1, agentId, "Pending Persona"));

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            resolver,
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(agentId),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId)).ConfigureAwait(false))
        {
            events.Add(streamEvent);
        }

        // The placeholder (AssistantPending source) already carried the agent name when it was created.
        AssertEx.NotNull(capturedPlaceholder);
        AssertEx.Equal("Pending Persona", capturedPlaceholder!.AgentName);
        // SSE order is unchanged by the hoist.
        var pendingIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantPending);
        var queuedIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantQueued);
        var streamingIndex = events.FindIndex(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantStreaming);
        AssertEx.True(pendingIndex >= 0 && queuedIndex > pendingIndex && streamingIndex > queuedIndex,
            "SSE order must stay UserMessagePersisted -> AssistantPending -> AssistantQueued -> AssistantStreaming after the hoist.");
    }

    [Test]
    public async Task SendMessage_WhenModeOffUneditedDefault_ConfigHashByteIdenticalToLegacy()
    {
        // Byte-identical mode-off: resolving through an UNEDITED seeded Default Assistant (embedded prompt + full offer
        // + null model/reasoning + version 1) must produce the SAME runtime-package config hash as the pre-change null
        // path (LoadResolvedSystemPrompt + full offer + version 1).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var defaultAssistantId = Guid.NewGuid();
        var offeredTool = CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}");
        var embeddedPrompt = LoadEmbeddedChatPrompt();

        // The real resolver over a store whose Default Assistant is the seeded, unedited embedded prompt (version 1,
        // empty allowed set → full offer per the default-slug branch).
        var store = Substitute.For<IAgentDefinitionStore>();
        var defaultAssistant = new AgentDefinitionRecord(defaultAssistantId,
            "Default Assistant",
            null,
            embeddedPrompt,
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null,
            1,
            10,
            10,
            PlaybookEnabled: false,
            Source: AgentDefinitionSource.Seeded,
            SeedSlug: "default-assistant");
        store.GetByIdAsync(defaultAssistantId, Arg.Any<CancellationToken>()).Returns(defaultAssistant);
        var offerProvider = CreateOfferProvider(offeredTool);
        var resolver = new AgentDefinitionResolver(store,
            CreateEmptyPlaybookStore(),
            CreateEmptySkillStore(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            NullLogger<AgentDefinitionResolver>.Instance);

        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            resolver,
            store,
            CreateDefaultAgentProvider(defaultAssistantId),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(embeddedPrompt, runner.LastSystemPrompt);
        AssertEx.Equal(1, runner.LastAgentDefinitionVersion);

        // Hand-build the legacy null-path package (embedded prompt + full offer + version 1) and compare config hashes.
        var builder = new LocalChatRuntimePackageBuilder();
        var legacyPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            conversationId,
            embeddedPrompt,
            [],
            new LocalChatAgentOptions().DefaultModel,
            1,
            AllowedTools: [offeredTool]));
        var resolvedPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            conversationId,
            runner.LastSystemPrompt!,
            [],
            new LocalChatAgentOptions().DefaultModel,
            runner.LastAgentDefinitionVersion,
            AllowedTools: runner.LastAllowedTools));

        AssertEx.Equal(legacyPackage.ConfigHash, resolvedPackage.ConfigHash);
    }

    [Test]
    public async Task SendMessage_WhenDefaultAssistantEdited_ConfigHashDiffers()
    {
        // Negative guard: editing the Default Assistant bumps its Version (AgentDefinitionStore.cs:131), so the resolved
        // package's config hash differs from the unedited (version 1) legacy package — mode-off is NOT frozen to the
        // embedded prompt once the operator edits the default (documented as intended, §15).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var defaultAssistantId = Guid.NewGuid();
        var offeredTool = CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}");
        var embeddedPrompt = LoadEmbeddedChatPrompt();

        var store = Substitute.For<IAgentDefinitionStore>();
        // An EDITED Default Assistant: a changed prompt and a bumped version (2).
        var editedDefault = new AgentDefinitionRecord(defaultAssistantId,
            "Default Assistant",
            null,
            embeddedPrompt + "\n\nExtra operator guidance.",
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null,
            2,
            10,
            20,
            PlaybookEnabled: false,
            Source: AgentDefinitionSource.Seeded,
            SeedSlug: "default-assistant");
        store.GetByIdAsync(defaultAssistantId, Arg.Any<CancellationToken>()).Returns(editedDefault);
        var offerProvider = CreateOfferProvider(offeredTool);
        var resolver = new AgentDefinitionResolver(store,
            CreateEmptyPlaybookStore(),
            CreateEmptySkillStore(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            NullLogger<AgentDefinitionResolver>.Instance);

        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            resolver,
            store,
            CreateDefaultAgentProvider(defaultAssistantId),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var builder = new LocalChatRuntimePackageBuilder();
        var legacyPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            conversationId,
            embeddedPrompt,
            [],
            new LocalChatAgentOptions().DefaultModel,
            1,
            AllowedTools: [offeredTool]));
        var resolvedPackage = builder.Build(new LocalChatRuntimePackageRequest(Guid.NewGuid(),
            conversationId,
            runner.LastSystemPrompt!,
            [],
            new LocalChatAgentOptions().DefaultModel,
            runner.LastAgentDefinitionVersion,
            AllowedTools: runner.LastAllowedTools));

        AssertEx.True(legacyPackage.ConfigHash != resolvedPackage.ConfigHash,
            "Editing the Default Assistant (prompt + version bump) must change the mode-off config hash.");
    }

    private static IPlaybookActionStore CreateEmptyPlaybookStore()
    {
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        return playbookStore;
    }

    private static IAgentSkillStore CreateEmptySkillStore()
    {
        var skillStore = Substitute.For<IAgentSkillStore>();
        skillStore.ListEnabledByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([]));
        return skillStore;
    }

    private static string LoadEmbeddedChatPrompt()
    {
        var assembly = typeof(LocalChatAgentOptions).Assembly;
        var resourceName = new LocalChatAgentOptions().InstructionsResource;
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Embedded instructions resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Test]
    public async Task SendMessageAsync_WhenRequestModelNull_ResolvesOfferWithOperatorNodeDefaultModel()
    {
        // MED-4 regression: a "Local default" send carries a null request model. The offer-time active model must
        // resolve to the operator's node-default selection (StoredNodeSettings.DefaultModelName), NOT the static
        // config fallback — otherwise a tool-capable node default would never reach the capability gate and
        // run_in_agent_home would be withheld even though the operator selected a tool-capable model.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider();

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore("qwen3:8b"),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        offerProvider.Received().GetOfferedTools("qwen3:8b");
        offerProvider.DidNotReceive().GetOfferedTools(new LocalChatAgentOptions().DefaultModel);
    }

    [Test]
    public async Task SendMessageAsync_WhenRequestModelNullAndNoNodeDefault_ResolvesOfferWithStaticConfigDefault()
    {
        // MED-4 lower bound: with no operator node default the offer-time active model falls through to the static
        // config fallback, so the pre-fix behaviour is preserved for a node that never set a default model.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider();

        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        offerProvider.Received().GetOfferedTools(new LocalChatAgentOptions().DefaultModel);
    }

    private static ILocalToolOfferProvider CreateOfferProvider(params AllowedToolDto[] tools)
    {
        var provider = Substitute.For<ILocalToolOfferProvider>();
        provider.GetOfferedTools(Arg.Any<string?>()).Returns(tools);
        return provider;
    }

    // The default node-settings store: no operator-selected node default, so model resolution falls through to the
    // request model (or the static config fallback). The capability-gate test supplies a tool-capable default.
    private static INodeSettingsStore CreateNodeSettingsStore(string? defaultModelName = null)
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            DefaultModelName = defaultModelName
        });
        return store;
    }

    // The default classification service: every model resolves to BOTH thinking- and tools-capable, so the existing
    // think/tool-offer assertions stay byte-identical (these tests pre-date per-model capability gating). The dedicated
    // capability-gate tests substitute an incapable classification.
    private static IModelClassificationService CreateModelClassificationService(params string[] capabilities)
    {
        var resolved = capabilities.Length > 0 ? capabilities : ["completion", "tools", "thinking"];
        var service = Substitute.For<IModelClassificationService>();
        service.ClassifyAsync(Arg.Any<IEnumerable<(string ModelName, string? Digest)>>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var models = callInfo.Arg<IEnumerable<(string ModelName, string? Digest)>>();
                   var map = new Dictionary<string, ModelClassificationResult>(StringComparer.OrdinalIgnoreCase);
                   foreach (var (modelName, _) in models)
                   {
                       if (!string.IsNullOrWhiteSpace(modelName) && !map.ContainsKey(modelName))
                       {
                           map[modelName] = new ModelClassificationResult(modelName, ModelKind.Chat, ModelKind.Chat, resolved, IsOverridden: false);
                       }
                   }

                   return Task.FromResult<IReadOnlyDictionary<string, ModelClassificationResult>>(map);
               });
        return service;
    }

    // The default (unbound) resolver: ResolveAsync returns null, so the service keeps today's literals — these tests
    // exercise the default chat persona. Bound-agent behavior is covered by the dedicated bound-conversation tests.
    private static IAgentDefinitionResolver CreateAgentDefinitionResolver()
    {
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((ResolvedAgentRuntime?)null);
        return resolver;
    }

    // The default-agent provider: returns null so the effective-agent precedence falls through to a null id (no seeded
    // Default Assistant in these unit tests), keeping the default-persona contract — the resolver is consulted with a
    // null binding exactly as before. The dedicated default-fallback test supplies a non-null id.
    private static IDefaultAgentProvider CreateDefaultAgentProvider(Guid? defaultAgentId = null)
    {
        var provider = Substitute.For<IDefaultAgentProvider>();
        provider.GetDefaultAgentIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(defaultAgentId));
        return provider;
    }

    // The default store/orchestration resolver: GetByIdAsync returns null so ResolveOrchestrationAsync never reaches the
    // orchestration resolver — the package carries no spec and the single-agent path is byte-identical.
    private static IAgentDefinitionStore CreateAgentDefinitionStore()
    {
        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
        return store;
    }

    private static IOrchestrationResolver CreateOrchestrationResolver()
    {
        var resolver = Substitute.For<IOrchestrationResolver>();
        resolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns((ResolvedOrchestration?)null);
        return resolver;
    }

    private static AgentDefinitionRecord CreateOrchestratorRecord(Guid id)
    {
        return new AgentDefinitionRecord(id,
            "Orchestrator",
            null,
            "Orchestrator prompt.",
            "qwen3:8b",
            null,
            AgentDefinitionKind.Orchestrator,
            [],
            new Dictionary<string, bool>(),
            null,
            4,
            10,
            10);
    }

    private static OrchestrationSpec CreateSampleSpec()
    {
        return new OrchestrationSpec
        {
            TriageParticipantKey = "a",
            MaxTurnsPerAgent = 6,
            ReturnToPrevious = false,
            Participants =
            [
                new OrchestrationSpecParticipant
                {
                    Key = "a",
                    Name = "Triage",
                    Instructions = "Triage.",
                    ModelId = "qwen3:8b",
                    Tools = []
                },
                new OrchestrationSpecParticipant
                {
                    Key = "b",
                    Name = "Specialist",
                    Instructions = "Specialist.",
                    ModelId = "qwen3:8b",
                    Tools = []
                }
            ],
            Edges =
            [
                new OrchestrationSpecEdge
                {
                    FromKey = "a",
                    ToKey = "b"
                }
            ]
        };
    }

    private static AllowedToolDto CreateLocalToolDto(string name, string parameterSchema)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = parameterSchema,
            RequiresApproval = false
        };
    }

    [Test]
    public async Task SendMessageAsync_WhenOlderVariantSelected_SendsSelectedVariantNotNewest()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();
        var newerVariantId = Guid.NewGuid();
        // Explicitly select the OLDER variant; the resolver would otherwise default to the newest sibling.
        var selectedPath = new Dictionary<Guid, Guid>
        {
            [variantGroupId] = olderVariantId
        };

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection: selectedPath,
            requestSelection: null).ConfigureAwait(false);

        var assistantContents = runner.CapturedContext
                                      .Where(message => message.Role == MessageRole.Assistant)
                                      .Select(message => message.Content)
                                      .ToList();
        AssertEx.Contains(assistantContents, "older answer");
        AssertEx.False(assistantContents.Contains("newer answer"), "The newest variant must be excluded when an older variant is selected.");
    }

    [Test]
    public async Task SendMessageAsync_WhenNoSelection_FallsBackToNewestVariant()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();
        var newerVariantId = Guid.NewGuid();

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection: null,
            requestSelection: null).ConfigureAwait(false);

        var assistantContents = runner.CapturedContext
                                      .Where(message => message.Role == MessageRole.Assistant)
                                      .Select(message => message.Content)
                                      .ToList();
        AssertEx.Contains(assistantContents, "newer answer");
        AssertEx.False(assistantContents.Contains("older answer"), "With no selection the newest variant is the default.");
    }

    [Test]
    public async Task SendMessageAsync_WhenRequestCarriesSelection_PersistsAndUsesIt()
    {
        var conversationId = Guid.NewGuid();
        var variantGroupId = Guid.NewGuid();
        var olderVariantId = Guid.NewGuid();
        var newerVariantId = Guid.NewGuid();
        // No persisted selection; the request rides a selection for the OLDER variant. The service must persist it
        // (SetSelectedPathAsync) and use it to build context.
        var requestSelection = new Dictionary<Guid, Guid>
        {
            [variantGroupId] = olderVariantId
        };

        var runner = await RunWithVariantConversationAsync(conversationId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection: null,
            requestSelection: requestSelection).ConfigureAwait(false);

        var assistantContents = runner.CapturedContext
                                      .Where(message => message.Role == MessageRole.Assistant)
                                      .Select(message => message.Content)
                                      .ToList();
        AssertEx.Contains(assistantContents, "older answer");
        AssertEx.True(runner.SelectionPersisted, "A request-supplied selection must be persisted via SetSelectedPathAsync.");
    }

    private static async Task<ContextCapturingInvocationRunner> RunWithVariantConversationAsync(Guid conversationId,
        Guid variantGroupId,
        Guid olderVariantId,
        Guid newerVariantId,
        IReadOnlyDictionary<Guid, Guid>? persistedSelection,
        IReadOnlyDictionary<Guid, Guid>? requestSelection)
    {
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreateVariantPersistence(conversationId,
            assistantMessageId,
            requestId,
            variantGroupId,
            olderVariantId,
            newerVariantId,
            persistedSelection);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new NodeChatInvocationPump(persistence, TimeProvider.System),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateAgentDefinitionResolver(),
            CreateAgentDefinitionStore(),
            CreateDefaultAgentProvider(),
            CreateOrchestrationResolver(),
            CreateNodeSettingsStore(),
            CreateModelClassificationService(),
            TimeProvider.System,
            NullLogger<NodeChatStreamService>.Instance);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "follow up",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           SelectedPath: requestSelection)).ConfigureAwait(false))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.True(runner.CaptureObserved, "Expected the invocation runner to observe the runtime package.");
        runner.SelectionPersisted = persistence.ReceivedCalls()
                                               .Any(call => call.GetMethodInfo().Name == nameof(INodeChatPersistenceService.SetSelectedPathAsync));
        return runner;
    }

    private static INodeChatPersistenceService CreateVariantPersistence(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        Guid variantGroupId,
        Guid olderVariantId,
        Guid newerVariantId,
        IReadOnlyDictionary<Guid, Guid>? persistedSelection)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();

        var userTurn = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            conversationId,
            null,
            0,
            "user",
            "original question",
            null,
            NodeChatMessageStatusValues.Completed,
            1,
            1,
            null,
            null,
            null);
        var olderVariant = new NodeChatPersistedMessageDto(olderVariantId,
            conversationId,
            Guid.NewGuid(),
            1,
            "assistant",
            "older answer",
            null,
            NodeChatMessageStatusValues.Completed,
            1,
            1,
            null,
            null,
            null,
            VariantGroupId: variantGroupId);
        var newerVariant = new NodeChatPersistedMessageDto(newerVariantId,
            conversationId,
            Guid.NewGuid(),
            2,
            "assistant",
            "newer answer",
            null,
            NodeChatMessageStatusValues.Completed,
            2,
            2,
            null,
            null,
            null,
            VariantGroupId: variantGroupId);

        var conversation = new NodeChatConversationDto(conversationId,
            "variant chat",
            null,
            1,
            1,
            false,
            [userTurn, olderVariant, newerVariant],
            SelectedPath: persistedSelection);
        var newUserMessage = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            conversationId,
            null,
            3,
            "user",
            "follow up",
            null,
            NodeChatMessageStatusValues.Completed,
            3,
            3,
            null,
            null,
            null);
        var assistantPending = CreateAssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Pending, string.Empty, null);

        persistence.GetConversationAsync(conversationId, Arg.Any<CancellationToken>()).Returns(conversation);
        persistence.SetSelectedPathAsync(Arg.Any<NodeChatSetSelectedPathRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => callInfo.ArgAt<NodeChatSetSelectedPathRequest>(0).SelectedPath ?? new Dictionary<Guid, Guid>());
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>()).Returns(newUserMessage);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>()).Returns(assistantPending);
        persistence.MarkAssistantQueuedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantPending with
                   {
                       Status = NodeChatMessageStatusValues.Queued
                   });
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantPending with
                   {
                       Status = NodeChatMessageStatusValues.Streaming
                   });
        persistence.FlushAssistantPartialAsync(Arg.Any<NodeChatPartialFlushRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => CreateAssistantMessage(conversationId, assistantMessageId, requestId, NodeChatMessageStatusValues.Streaming,
                       callInfo.ArgAt<NodeChatPartialFlushRequest>(0).Content, null));
        persistence.TerminalizeAssistantMessageAsync(Arg.Any<NodeChatTerminalizeMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo => CreateAssistantMessage(conversationId, assistantMessageId, requestId, callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0).Status,
                       callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0).Content ?? string.Empty, null));

        return persistence;
    }

    private static INodeChatPersistenceService CreatePersistence(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        Action<NodeChatTerminalizeMessageRequest> terminalized,
        Guid? agentDefinitionId = null,
        Action<NodeChatCreateAssistantPlaceholderRequest>? placeholderObserver = null)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        var conversation = new NodeChatConversationDto(conversationId,
            "test",
            null,
            1,
            1,
            false,
            [],
            AgentDefinitionId: agentDefinitionId);
        var userMessage = new NodeChatPersistedMessageDto(Guid.NewGuid(),
            conversationId,
            null,
            1,
            "user",
            "hello",
            null,
            NodeChatMessageStatusValues.Completed,
            1,
            1,
            null,
            null,
            null);
        var assistantPending = CreateAssistantMessage(conversationId,
            assistantMessageId,
            requestId,
            NodeChatMessageStatusValues.Pending,
            string.Empty,
            null);
        var assistantQueued = assistantPending with
        {
            Status = NodeChatMessageStatusValues.Queued
        };
        var assistantStreaming = assistantPending with
        {
            Status = NodeChatMessageStatusValues.Streaming
        };

        persistence.GetConversationAsync(conversationId, Arg.Any<CancellationToken>())
                   .Returns(conversation);
        persistence.PersistUserMessageAsync(Arg.Any<NodeChatPersistUserMessageRequest>(), Arg.Any<CancellationToken>())
                   .Returns(userMessage);
        persistence.CreateAssistantPlaceholderAsync(Arg.Any<NodeChatCreateAssistantPlaceholderRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatCreateAssistantPlaceholderRequest>(0);
                       placeholderObserver?.Invoke(request);
                       // Reflect the stamped attribution into the returned pending DTO so the AssistantPending event
                       // carries the agent name a real placeholder would.
                       return assistantPending with
                       {
                           AgentDefinitionId = request.AgentDefinitionId,
                           AgentName = request.AgentName
                       };
                   });
        persistence.MarkAssistantQueuedAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantQueued);
        persistence.MarkAssistantStreamingAsync(Arg.Any<NodeChatMessageCorrelation>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(assistantStreaming);
        persistence.FlushAssistantPartialAsync(Arg.Any<NodeChatPartialFlushRequest>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatPartialFlushRequest>(0);
                       return CreateAssistantMessage(conversationId,
                           assistantMessageId,
                           requestId,
                           NodeChatMessageStatusValues.Streaming,
                           request.Content,
                           request.Reasoning);
                   });
        persistence.TerminalizeAssistantMessageAsync(Arg.Do<NodeChatTerminalizeMessageRequest>(terminalized), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       var request = callInfo.ArgAt<NodeChatTerminalizeMessageRequest>(0);
                       return CreateAssistantMessage(conversationId,
                           assistantMessageId,
                           requestId,
                           request.Status,
                           request.Content ?? string.Empty,
                           request.Reasoning,
                           request.Error);
                   });

        return persistence;
    }

    private static NodeChatPersistedMessageDto CreateAssistantMessage(Guid conversationId,
        Guid assistantMessageId,
        Guid requestId,
        string status,
        string content,
        string? reasoning,
        string? error = null)
    {
        return new NodeChatPersistedMessageDto(assistantMessageId,
            conversationId,
            requestId,
            2,
            "assistant",
            content,
            reasoning,
            status,
            1,
            1,
            null,
            error,
            null);
    }

    private sealed class StreamingUntilCancelledInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportInvocationThinkingChunkAsync(context.Package.InvocationId, "thinking").ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class GatedCompletingInvocationRunner(RecordingWorkerEventDispatcher dispatcher, Task release) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            // Emit a delta so the consumer can disconnect, then block until released to report the real Completed
            // terminal. This reproduces a client disconnecting while the shared runner is still working.
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await release.ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class ContextCapturingInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        // The conversation context assembled onto the runtime package; the selected-path tests assert which
        // variant the service included.
        public IReadOnlyList<ConversationMessageDto> CapturedContext { get; private set; } = [];

        public bool CaptureObserved { get; private set; }

        // Set by RunWithVariantConversationAsync after the run, from a NSubstitute Received() check on the mock —
        // exposed here so the test reads one object.
        public bool SelectionPersisted { get; set; }
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            CapturedContext = context.Package.ConversationContext;
            CaptureObserved = true;
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class CompletingInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationThinkingChunkAsync(context.Package.InvocationId, "thinking").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class PackageCapturingInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        // Captures the runtime-package fields the binding hydration drives: the system prompt, the agent-definition
        // version, the reasoning effort, and the offered tool list. The bound/unbound tests assert on these.
        public string? LastSystemPrompt { get; private set; }
        public int LastAgentDefinitionVersion { get; private set; }
        public string? LastReasoningEffort { get; private set; }
        public IReadOnlyList<AllowedToolDto> LastAllowedTools { get; private set; } = [];
        public OrchestrationSpec? LastOrchestrationSpec { get; private set; }
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastSystemPrompt = context.Package.ResolvedSystemPrompt;
            LastAgentDefinitionVersion = context.Package.AgentDefinitionVersion;
            LastReasoningEffort = context.Package.ReasoningEffort;
            LastAllowedTools = context.Package.AllowedTools;
            LastOrchestrationSpec = context.Package.OrchestrationSpec;
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class ReasoningCapturingInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        // The reasoning effort carried on the runtime package handed to the invocation; the test asserts the
        // value selected on the send request reaches the runtime package (or stays null when none was selected).
        public string? LastReasoningEffort { get; private set; }

        // The offer list carried on the runtime package; the test asserts the local tool catalog reaches the
        // runtime package only when the client opted in.
        public IReadOnlyList<AllowedToolDto> LastAllowedTools { get; private set; } = [];

        // The sampling overrides carried on the runtime package; the test asserts per-send sampling reaches the
        // runtime package (or stays null when none was selected).
        public SamplingOptions? LastSamplingOptions { get; private set; }

        public bool CaptureObserved { get; private set; }
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            LastReasoningEffort = context.Package.ReasoningEffort;
            LastAllowedTools = context.Package.AllowedTools;
            LastSamplingOptions = context.Package.SamplingOptions;
            CaptureObserved = true;
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class ToolEmittingInvocationRunner(RecordingWorkerEventDispatcher dispatcher) : IInvocationRunner
    {
        public int ActiveInvocationCount => 0;

        public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
        {
            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = context.Package.InvocationId,
                ToolCallId = "call-1",
                ToolName = "weather",
                Phase = ToolCallLifecyclePhase.Requested,
                Arguments = "{\"city\":\"berlin\"}",
                RequiresApproval = false
            }).ConfigureAwait(false);
            await dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
            {
                InvocationId = context.Package.InvocationId,
                ToolCallId = "call-1",
                ToolName = "weather",
                Phase = ToolCallLifecyclePhase.Completed,
                Result = "sunny",
                IsError = false
            }).ConfigureAwait(false);
            await dispatcher.ReportInvocationStreamChunkAsync(context.Package.InvocationId, "answer").ConfigureAwait(false);
            await dispatcher.ReportInvocationCompletedAsync(context.Package.InvocationId, 10, 3, 13, 1).ConfigureAwait(false);
        }

        public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(string.Empty);
        }

        public void Cancel(Guid invocationId)
        {
        }

        public void CancelAll()
        {
        }

        public void CleanupStaleToolCalls(TimeSpan maxAge)
        {
        }

        public void ResolveApprovalResult(ApprovalResolvedEvent evt)
        {
        }

        public void ResolveToolCallResult(ToolCallResultEvent evt)
        {
        }
    }

    private sealed class RecordingWorkerEventDispatcher : IWorkerEventDispatcher
    {
        public event EventHandler<InvocationStateChangedEventArgs>? InvocationStateChanged;

        public event EventHandler<ToolCallLifecycleChangedEventArgs>? ToolCallLifecycleChanged;

        public InvocationState? CurrentInvocation { get; private set; }

        public bool IsAcceptingRemoteInvocations => true;

        public void StopAcceptingRemoteInvocations()
        {
        }

        public Task DispatchInvocationAssignedAsync(EncryptedRuntimePackageDto package)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationAssignedV2Async(InvocationAssignedEnvelope envelope)
        {
            return Task.CompletedTask;
        }

        public Task DispatchToolCallResultAsync(ToolCallResultEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchDisconnectRequestedAsync(DisconnectRequestedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchApprovalResolvedAsync(ApprovalResolvedEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task DispatchInvocationCancelledAsync(InvocationCancelledEvent evt)
        {
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> ReportInvocationAssignedAsync(RuntimePackage package, CancellationToken cancellationToken = default)
        {
            CurrentInvocation = new InvocationState
            {
                InvocationId = package.InvocationId,
                ConversationId = package.ConversationId,
                Status = InvocationStatus.Assigned,
                StartedAt = DateTimeOffset.UtcNow,
                LastUpdatedAt = DateTimeOffset.UtcNow
            };
            RaiseChanged();
            return Task.FromResult<IAsyncDisposable>(NoopLease.Instance);
        }

        public Task ReportInvocationStreamChunkAsync(Guid invocationId, string chunk)
        {
            if (CurrentInvocation is null)
            {
                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Running;
            CurrentInvocation.StreamedContent += chunk;
            CurrentInvocation.StreamedChunkCount++;
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationThinkingChunkAsync(Guid invocationId, string chunk)
        {
            if (CurrentInvocation is null)
            {
                return Task.CompletedTask;
            }

            CurrentInvocation.Status = InvocationStatus.Running;
            CurrentInvocation.StreamedThinkingContent += chunk;
            CurrentInvocation.StreamedThinkingChunkCount++;
            CurrentInvocation.LastUpdatedAt = DateTimeOffset.UtcNow;
            RaiseChanged();
            return Task.CompletedTask;
        }

        public Task ReportInvocationCompletedAsync(Guid invocationId, int? inputTokens = null, int? outputTokens = null, int? totalTokens = null, int? reasoningTokens = null,
            long? generationDurationMs = null)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = InvocationStatus.Completed;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                CurrentInvocation.InputTokens = inputTokens;
                CurrentInvocation.OutputTokens = outputTokens;
                CurrentInvocation.TotalTokens = totalTokens;
                CurrentInvocation.ReasoningTokens = reasoningTokens;
                CurrentInvocation.GenerationDurationMs = generationDurationMs;
                RaiseChanged();
            }

            return Task.CompletedTask;
        }

        public Task ReportInvocationFailedAsync(Guid invocationId, string failureMessage, FailureCategory failureCategory)
        {
            if (CurrentInvocation is not null)
            {
                CurrentInvocation.Status = failureCategory == FailureCategory.Cancelled ? InvocationStatus.Cancelled : InvocationStatus.Failed;
                CurrentInvocation.Error = failureMessage;
                CurrentInvocation.FailureCategory = failureCategory;
                CurrentInvocation.CompletedAt = DateTimeOffset.UtcNow;
                RaiseChanged();
            }

            return Task.CompletedTask;
        }

        public Task ReportToolCallRequestedAsync(ToolCallRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportApprovalRequestedAsync(ApprovalRequestPayload payload)
        {
            return Task.CompletedTask;
        }

        public Task ReportToolCallLifecycleAsync(ToolCallLifecyclePayload payload)
        {
            ToolCallLifecycleChanged?.Invoke(this, new ToolCallLifecycleChangedEventArgs(payload));
            return Task.CompletedTask;
        }

        private void RaiseChanged()
        {
            InvocationStateChanged?.Invoke(this, new InvocationStateChangedEventArgs(CurrentInvocation!));
        }

        private sealed class NoopLease : IAsyncDisposable
        {
            public static readonly NoopLease Instance = new();

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
