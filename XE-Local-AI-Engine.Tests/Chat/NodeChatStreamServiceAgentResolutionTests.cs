namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using static XE_Local_AI_Engine.Tests.Chat.NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceAgentResolutionTests
{
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
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Bound persona prompt.", [boundTool], "qwen3:8b", "high", AgentDefinitionVersion: 9));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}")),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal("Bound persona prompt.", runner.LastSystemPrompt);
        AssertEx.Equal(expected: 9, runner.LastAgentDefinitionVersion);
        AssertEx.Equal("high", runner.LastReasoningEffort);
        AssertEx.Equal(expected: 1, runner.LastAllowedTools.Count);
        AssertEx.Equal("Calculate", runner.LastAllowedTools[0].Name);
        AssertEx.True(runner.LastOrchestrationSpec is null, "A single-agent binding must carry no orchestration spec.");
        // The just-sent user turn ("hello") is threaded to the resolver as the relevance-retrieval query —
        // not just any string, the actual turn content drives which playbook actions are injected.
        await resolver.Received().ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Is<string?>(query => query == "hello"), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessageAsync_WhenMemoryExtractionEnabled_DispatchesExtractionOnCompletion()
    {
        // Positive control for the extraction gate: a playbook-enabled agent that ALSO opts into extraction mines the
        // completed run — the dispatcher receives one Dispatch when the run completes.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);

        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Bound persona prompt.", [], "qwen3:8b", ReasoningEffort: null, AgentDefinitionVersion: 9, agentDefinitionId, "Memory Agent", PlaybookEnabled: true,
                    MemoryExtractionEnabled: true));
        var extractionDispatcher = Substitute.For<IMemoryExtractionDispatcher>();

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            extractionDispatcher,
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
        extractionDispatcher.Received(1).Dispatch(Arg.Any<MemoryExtractionDispatchContext>(), Arg.Any<MemoryExtractionRunInput>());
    }

    [Test]
    public async Task SendMessageAsync_WhenMemoryExtractionDisabled_SkipsExtractionButStillResolvesMemory()
    {
        // A retrieval-only agent (PlaybookEnabled=true, MemoryExtractionEnabled=false) still resolves
        // its definition (so its existing enabled memory is injected via the resolved prompt) but its completed run is
        // NOT mined — the dispatcher is never called, so no extraction round-trip happens.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);

        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Bound persona prompt.", [], "qwen3:8b", ReasoningEffort: null, AgentDefinitionVersion: 9, agentDefinitionId, "Retrieval Only", PlaybookEnabled: true,
                    MemoryExtractionEnabled: false));
        var extractionDispatcher = Substitute.For<IMemoryExtractionDispatcher>();

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            extractionDispatcher,
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
        // Retrieval still happens — the definition was resolved with the user turn as the relevance query, so existing
        // memory rides the resolved prompt.
        await resolver.Received().ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Is<string?>(query => query == "hello"), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
        // …but no NEW candidates are mined: extraction is never dispatched.
        extractionDispatcher.DidNotReceive().Dispatch(Arg.Any<MemoryExtractionDispatchContext>(), Arg.Any<MemoryExtractionRunInput>());
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
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
        AssertEx.Equal(expected: 1, runner.LastAgentDefinitionVersion);
        AssertEx.NotNullOrEmpty(runner.LastSystemPrompt);
        await resolver.Received().ResolveAsync(agentDefinitionId: null, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Selected persona.", [], "qwen3:8b", ReasoningEffort: null, AgentDefinitionVersion: 3, requestAgentId, "Selected Agent"));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
                           AgentDefinitionId: requestAgentId)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        await resolver.Received().ResolveAsync(requestAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await resolver.DidNotReceive().ResolveAsync(conversationAgentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Default persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, defaultAssistantId, "Default Assistant"));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(defaultAssistantId),
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
        await resolver.Received().ResolveAsync(defaultAssistantId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Backend Buddy"));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(agentId),
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
        AssertEx.NotNull(capturedPlaceholder);
        AssertEx.Equal(agentId, capturedPlaceholder!.AgentDefinitionId);
        AssertEx.Equal("Backend Buddy", capturedPlaceholder.AgentName);
    }

    [Test]
    public async Task SendMessage_PendingMessageCarriesAgentName()
    {
        // Locks the resolve-before-placeholder ordering: the resolve happens BEFORE the placeholder is minted, so the
        // placeholder request (the AssistantPending source) already carries the agent name — not just the terminal.
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
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Pending Persona"));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(agentId),
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
    public async Task SendMessage_WhenUserPicksConcreteModel_SuppressesAgentPin_StampsDropdownModelEverywhere()
    {
        // The bug: a bound agent pins its own ModelProfile, but the user ALSO picked a concrete model in the chat
        // dropdown. The explicit dropdown pick must WIN — for the run (package model) AND the persisted attribution
        // (placeholder + terminal model) — and the resolver must be told honorModelProfile=false so the pin is
        // suppressed. The resolver mock mirrors the real resolver: it returns a null ModelProfile when the pin is
        // suppressed, so `resolved?.ModelProfile ?? activeModel` yields the user's pick.
        const string dropdownModel = "qwen2.5:7b";
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        NodeChatCreateAssistantPlaceholderRequest? capturedPlaceholder = null;
        NodeChatTerminalizeMessageRequest? terminalRequest = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request, agentDefinitionId,
            placeholderObserver: request => capturedPlaceholder = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var honorModelProfile = callInfo.ArgAt<bool>(4);
                    // The agent pins "gemma3:4b"; when the pin is suppressed the resolver projects a null ModelProfile.
                    return new ResolvedAgentRuntime("Pinned persona.", [], honorModelProfile ? "gemma3:4b" : null, ReasoningEffort: null, AgentDefinitionVersion: 9, agentDefinitionId, "Pinned Agent");
                });

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
                           Model: dropdownModel,
                           MessageId: assistantMessageId,
                           RequestId: requestId)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        // The run executed on the dropdown model, not the agent's pin.
        AssertEx.Equal(dropdownModel, runner.LastModelProfile);
        // Both the placeholder (UI label from the first frame) and the persisted terminal carry the dropdown model.
        AssertEx.NotNull(capturedPlaceholder);
        AssertEx.Equal(dropdownModel, capturedPlaceholder!.Model);
        AssertEx.NotNull(terminalRequest);
        AssertEx.Equal(dropdownModel, terminalRequest!.Model);
        // The resolver was told to suppress the pin AND gate by the dropdown model.
        await resolver.Received().ResolveAsync(agentDefinitionId,
            Arg.Is<string?>(model => model == dropdownModel),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Is<bool>(honor => !honor),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessage_WhenNoExplicitPick_HonorsAgentPin_StampsPinnedModelEverywhere()
    {
        // The companion case: no concrete dropdown pick (request.Model null = "Local default"), so the bound agent's
        // pinned ModelProfile applies — for the run AND the persisted attribution — and the resolver is told
        // honorModelProfile=true (the pin is honored and projected).
        const string pinnedModel = "gemma3:4b";
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        NodeChatCreateAssistantPlaceholderRequest? capturedPlaceholder = null;
        NodeChatTerminalizeMessageRequest? terminalRequest = null;
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, request => terminalRequest = request, agentDefinitionId,
            placeholderObserver: request => capturedPlaceholder = request);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var honorModelProfile = callInfo.ArgAt<bool>(4);
                    return new ResolvedAgentRuntime("Pinned persona.", [], honorModelProfile ? pinnedModel : null, ReasoningEffort: null, AgentDefinitionVersion: 9, agentDefinitionId, "Pinned Agent");
                });

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
            // The local-default resolver would return some installed GGUF; the pin must still win over it.
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
        AssertEx.Equal(pinnedModel, runner.LastModelProfile);
        AssertEx.NotNull(capturedPlaceholder);
        AssertEx.Equal(pinnedModel, capturedPlaceholder!.Model);
        AssertEx.NotNull(terminalRequest);
        AssertEx.Equal(pinnedModel, terminalRequest!.Model);
        await resolver.Received().ResolveAsync(agentDefinitionId,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Is<bool>(honor => honor),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
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
        var defaultAssistant = new AgentDefinitionRecord
        {
            Id = defaultAssistantId,
            Name = "Default Assistant",
            Description = null,
            Instructions = embeddedPrompt,
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10,
            PlaybookEnabled = false,
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = "default-assistant"
        };
        store.GetByIdAsync(defaultAssistantId, Arg.Any<CancellationToken>()).Returns(defaultAssistant);
        var offerProvider = CreateOfferProvider(offeredTool);
        var resolver = new AgentDefinitionResolver(store,
            CreateEmptyPlaybookStore(),
            CreateEmptySkillStore(),
            Substitute.For<ICustomToolStore>(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            new FakeAgentInstructionProvider(),
            Substitute.For<IModelCapabilityResolver>(),
            new PermissiveToolApprovalPolicy(),
            StubNodeRuntimeSettings.Create().Build(),
            NullLogger<AgentDefinitionResolver>.Instance);

        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, store, CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(defaultAssistantId),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(embeddedPrompt, runner.LastSystemPrompt);
        AssertEx.Equal(expected: 1, runner.LastAgentDefinitionVersion);

        // Hand-build the legacy null-path package (embedded prompt + full offer + version 1) and compare config hashes.
        var builder = new LocalChatRuntimePackageBuilder();
        var legacyPackage = builder.Build(new LocalChatRuntimePackageRequest
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            ResolvedSystemPrompt = embeddedPrompt,
            ConversationContext = [],
            ModelProfile = new LocalChatAgentOptions().DefaultModel,
            AgentDefinitionVersion = 1,
            AllowedTools = [offeredTool]
        });
        var resolvedPackage = builder.Build(new LocalChatRuntimePackageRequest
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            ResolvedSystemPrompt = runner.LastSystemPrompt!,
            ConversationContext = [],
            ModelProfile = new LocalChatAgentOptions().DefaultModel,
            AgentDefinitionVersion = runner.LastAgentDefinitionVersion,
            AllowedTools = runner.LastAllowedTools
        });

        AssertEx.Equal(legacyPackage.ConfigHash, resolvedPackage.ConfigHash);
    }

    [Test]
    public async Task SendMessage_WhenDefaultAssistantEdited_ConfigHashDiffers()
    {
        // Negative guard: editing the Default Assistant bumps its Version (AgentDefinitionStore.UpdateAsync), so the
        // resolved package's config hash differs from the unedited (version 1) legacy package — mode-off is NOT frozen
        // to the embedded prompt once the operator edits the default (this is intended behavior).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var defaultAssistantId = Guid.NewGuid();
        var offeredTool = CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}");
        var embeddedPrompt = LoadEmbeddedChatPrompt();

        var store = Substitute.For<IAgentDefinitionStore>();
        // An EDITED Default Assistant: a changed prompt and a bumped version (2).
        var editedDefault = new AgentDefinitionRecord
        {
            Id = defaultAssistantId,
            Name = "Default Assistant",
            Description = null,
            Instructions = embeddedPrompt + "\n\nExtra operator guidance.",
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null,
            Version = 2,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 20,
            PlaybookEnabled = false,
            Source = AgentDefinitionSource.Seeded,
            SeedSlug = "default-assistant"
        };
        store.GetByIdAsync(defaultAssistantId, Arg.Any<CancellationToken>()).Returns(editedDefault);
        var offerProvider = CreateOfferProvider(offeredTool);
        var resolver = new AgentDefinitionResolver(store,
            CreateEmptyPlaybookStore(),
            CreateEmptySkillStore(),
            Substitute.For<ICustomToolStore>(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            new FakeAgentInstructionProvider(),
            Substitute.For<IModelCapabilityResolver>(),
            new PermissiveToolApprovalPolicy(),
            StubNodeRuntimeSettings.Create().Build(),
            NullLogger<AgentDefinitionResolver>.Instance);

        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(resolver, store, CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(defaultAssistantId),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var builder = new LocalChatRuntimePackageBuilder();
        var legacyPackage = builder.Build(new LocalChatRuntimePackageRequest
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            ResolvedSystemPrompt = embeddedPrompt,
            ConversationContext = [],
            ModelProfile = new LocalChatAgentOptions().DefaultModel,
            AgentDefinitionVersion = 1,
            AllowedTools = [offeredTool]
        });
        var resolvedPackage = builder.Build(new LocalChatRuntimePackageRequest
        {
            InvocationId = Guid.NewGuid(),
            ConversationId = conversationId,
            ResolvedSystemPrompt = runner.LastSystemPrompt!,
            ConversationContext = [],
            ModelProfile = new LocalChatAgentOptions().DefaultModel,
            AgentDefinitionVersion = runner.LastAgentDefinitionVersion,
            AllowedTools = runner.LastAllowedTools
        });

        AssertEx.True(legacyPackage.ConfigHash != resolvedPackage.ConfigHash,
            "Editing the Default Assistant (prompt + version bump) must change the mode-off config hash.");
    }

    [Test]
    public async Task SendMessageAsync_WhenRequestModelNull_ResolvesOfferWithOperatorNodeDefaultModel()
    {
        // Regression guard: a "Local default" send carries a null request model. The offer-time active model must
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
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore("qwen3:8b"),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        _ = offerProvider.Received().GetOfferedToolsAsync("qwen3:8b", Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>());
        _ = offerProvider.DidNotReceive().GetOfferedToolsAsync(new LocalChatAgentOptions().DefaultModel, Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SendMessageAsync_WhenLocalDefaultAndNoChatModelInstalled_TerminalizesAsModelNotInstalled()
    {
        // A "Local runtime default" send (request.Model null) where the resolver finds NO installed GGUF chat model
        // must fail BEFORE any provider invocation with FailureCategory.ModelNotInstalled — never the generic
        // ProviderUnreachable/Unexpected (the stale-id "Provider unreachable." regression).
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
            // Resolver reports no installed GGUF chat model (null), regardless of the persisted node default.
            CreateLocalDefaultChatModelResolver(resolved: null, echoPersistedDefault: false),
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

        AssertEx.True(events.Count > 0, "Expected the send to stream events.");
        AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.Equal(FailureCategory.ModelNotInstalled, dispatcher.CurrentInvocation!.FailureCategory);
        AssertEx.Equal(InvocationStatus.Failed, dispatcher.CurrentInvocation.Status);
        // The pre-run category reaches the client on the terminal event, so the SPA can show its localized headline.
        var terminal = events.Single(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantFailed);
        AssertEx.Equal(nameof(FailureCategory.ModelNotInstalled), terminal.FailureCategory);
    }

    [Test]
    public async Task SendMessage_WhenNoInstalledGgufButAgentPinsModel_RoutesThePin()
    {
        // The server-initiated companion to the case above: the node has NO installed GGUF chat model, but the bound
        // agent pins a (non-GGUF, e.g. Ollama) model. The turn has a model to run, so it must route the pin instead of
        // failing with FailureCategory.ModelNotInstalled — the work-session step path, which never sets request.Model.
        // It must ALSO offer that pin's tools: capabilities keyed on the null active model would report not-capable and
        // strip the offer, leaving a work session unable to call update_work_plan/record_finding/complete_work_session.
        const string pinnedModel = "llama3.1:8b";
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentDefinitionId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    // Mirrors the real resolver: an incapable model projects an EMPTY offer, so a non-empty offer here
                    // proves supportsTools:true reached it.
                    var supportsTools = callInfo.ArgAt<bool>(3);
                    var honorModelProfile = callInfo.ArgAt<bool>(4);
                    return new ResolvedAgentRuntime("Pinned persona.",
                        supportsTools ? [CreateLocalToolDto("record_finding", "{}")] : [],
                        honorModelProfile ? pinnedModel : null,
                        ReasoningEffort: null,
                        AgentDefinitionVersion: 9,
                        agentDefinitionId,
                        "Pinned Agent");
                });

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            // The store carries the same pin the definition resolver reports: with no active model it is the capability head.
            new ChatTurnResolver(resolver, CreateAgentDefinitionStore(agentDefinitionId, pinnedModel), CreateOrchestrationResolver(), CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions()),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            CreateOfferProvider(),
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            // Resolver reports no installed GGUF chat model (null) — the pin is the only model this turn has.
            CreateLocalDefaultChatModelResolver(resolved: null, echoPersistedDefault: false),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(pinnedModel, runner.LastModelProfile);
        AssertEx.True(runner.LastAllowedTools.Count > 0, "Expected the pin's tool offer to reach the package.");
        AssertEx.NotNull(dispatcher.CurrentInvocation);
        AssertEx.NotEqual(FailureCategory.ModelNotInstalled, dispatcher.CurrentInvocation!.FailureCategory);
    }

    [Test]
    public async Task SendMessageAsync_WhenLocalDefaultResolvesInstalledGgufModel_RoutesThatModel()
    {
        // A "Local runtime default" send where the resolver returns an installed GGUF chat model routes the turn on
        // that model — the offer-time active model is the resolved GGUF, NOT the static config fallback or Ollama.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider();

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            CreateDefaultAgentProvider(),
            CreateNodeSettingsStore(),
            CreateLocalDefaultChatModelResolver("phi-4:Q4_K_M", echoPersistedDefault: false),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        // The resolved installed GGUF — not the static config default — drives the offer-time active model.
        _ = offerProvider.Received().GetOfferedToolsAsync("phi-4:Q4_K_M", Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>());
        _ = offerProvider.DidNotReceive().GetOfferedToolsAsync(new LocalChatAgentOptions().DefaultModel, Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(InvocationStatus.Completed, dispatcher.CurrentInvocation!.Status);
    }

    [Test]
    public async Task SendMessageAsync_WhenActiveModelIsCodexCloudModel_SkipsOllamaClassificationForCapabilities()
    {
        // Capability gating: a Codex cloud model is not an Ollama model. The capability gate must use the Codex
        // provider's declared matrix (CodexProviderCapabilities.V0), NOT the Ollama /api/show classification — so the
        // classification service is never consulted for a Codex model id (which the local runtime has never seen).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new CompletingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider();
        var classificationService = CreateModelClassificationService();

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(classification: classificationService),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(true).Build(),
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
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
                           Model: "gpt-5.6-sol",
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");

        // The Ollama classifier is never consulted for a Codex model id — capabilities come from the Codex matrix.
        await classificationService.DidNotReceive()
                                   .ClassifyAsync(Arg.Is<IEnumerable<ModelIdentity>>(models => models.Any(m => string.Equals(m.ModelName, "gpt-5.6-sol", StringComparison.OrdinalIgnoreCase))),
                                       Arg.Any<CancellationToken>());

        // Tool calling is enabled for ALL Codex ids, so the requested local tool offer (UseLocalTools: true) is
        // honored for the Codex model — capabilities still come from the Codex matrix, not the Ollama classifier. The
        // offer is requested with isCloudModel: true so the knowledge-tool provider-locality gate applies.
        _ = offerProvider.Received().GetOfferedToolsAsync("gpt-5.6-sol", true, Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The pre-send context estimate must measure the request a send really makes: for the same model and agent it
    ///     resolves the system prompt and tool offer this service hands the runner.
    /// </summary>
    /// <remarks>Covers a bound narrowing agent and the unbound embedded default persona with the base scaffold.</remarks>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendMessageAsync_ResolvesTheSamePromptAndToolOfferAsTheContextEstimate(bool boundAgent)
    {
        const string model = "parity-model";
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        Guid? agentId = boundAgent ? Guid.NewGuid() : null;
        var narrowTool = CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}");
        var offerProvider = CreateOfferProvider(narrowTool, CreateLocalToolDto("lookup_note", "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}"));
        var agentResolver = CreateAgentDefinitionResolver();
        if (agentId is { } boundId)
        {
            agentResolver.ResolveAsync(boundId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                         .Returns(new ResolvedAgentRuntime("Narrow agent persona.", [narrowTool], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 3, boundId, "Narrow Agent"));
        }

        var turnResolver = new ChatTurnResolver(agentResolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(), CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance);
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithEnableTools(true).Build();
        var localChatOptions = Options.Create(new LocalChatAgentOptions
        {
            EnableTools = true
        });
        var defaultAgentProvider = CreateDefaultAgentProvider();
        var nodeSettingsStore = CreateNodeSettingsStore();
        var localDefaultResolver = CreateLocalDefaultChatModelResolver();
        var approvalPolicy = new PermissiveToolApprovalPolicy();

        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            turnResolver,
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            localChatOptions,
            runtimeSettings,
            new NodeChatStreamCancellationRegistry(),
            offerProvider,
            defaultAgentProvider,
            nodeSettingsStore,
            localDefaultResolver,
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            approvalPolicy,
            Substitute.For<IGraphWorkflowStore>(),
            NullLogger<NodeChatStreamService>.Instance,
            new GpuWorkGate());

        var detailsResolver = Substitute.For<ILocalModelDetailsResolver>();
        detailsResolver.ResolveAsync(model, Arg.Any<CancellationToken>())
                       .Returns(new LocalModelDetailsResolution.Ollama(new OllamaModelDetails
                       {
                           MaxContextTokens = 32768,
                           Capabilities = ["completion", "tools"]
                       }));
        var estimator = new HeuristicTokenEstimator();
        var estimateService = new ChatContextEstimateService(turnResolver,
            defaultAgentProvider,
            nodeSettingsStore,
            localDefaultResolver,
            runtimeSettings,
            offerProvider,
            approvalPolicy,
            localChatOptions,
            detailsResolver,
            estimator,
            Options.Create(new ConversationContextBudgetOptions()));

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: model,
                           UseLocalTools: true,
                           AgentDefinitionId: agentId)))
        {
            drained++;
        }

        var estimateInputs = await estimateService.ResolveAsync(model, agentId, useLocalTools: true, CancellationToken.None);
        var estimatePrompt = estimateInputs.SystemPrompt;
        var estimateOffer = estimateInputs.Offer;
        var estimate = AssertEx.NotNull(await estimateService.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = model,
            AgentId = agentId
        }, CancellationToken.None));

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var sentPrompt = AssertEx.NotNull(runner.LastSystemPrompt);
        AssertEx.Equal(sentPrompt, estimatePrompt);
        var sentToolNames = string.Join(",", runner.LastAllowedTools.Select(static tool => tool.Name));
        AssertEx.Equal(sentToolNames, string.Join(",", (estimateOffer.AllowedTools ?? []).Select(static tool => tool.Name)));
        AssertEx.Equal(sentToolNames, string.Join(",", estimate.Tools.Select(static tool => tool.Name)));
        AssertEx.Equal(boundAgent ? "GetCurrentTime" : "GetCurrentTime,lookup_note", sentToolNames);
        AssertEx.Equal(ConversationContextBudgeter.EstimateSystemPromptTokens(estimator, sentPrompt, estimator.ResolveDivisor(model)), estimate.Estimated!.SystemPromptTokens);
    }
}
