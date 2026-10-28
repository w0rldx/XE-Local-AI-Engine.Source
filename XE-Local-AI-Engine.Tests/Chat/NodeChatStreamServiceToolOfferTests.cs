namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceToolOfferTests
{
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
        AssertEx.Equal(expected: 2, runner.LastAllowedTools.Count);
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == "GetCurrentTime");
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == "Calculate");
        foreach (var tool in runner.LastAllowedTools)
        {
            AssertEx.Equal(ToolLocation.ClientLocal, tool.Location);
            AssertEx.NotNullOrEmpty(tool.ParameterSchema);
        }
    }

    [Test]
    public async Task SendMessageAsync_WhenOperatorToolsAreSuppressed_WithholdsAskUserAndTheWebToolsFromTheRuntimePackage()
    {
        // The workflow-owned work-session send: no operator is attached to answer a question or review a web result, so
        // those tools are withdrawn rather than left to park the turn. Everything else the turn was offered survives.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto(AskUserTool.ToolName, "{\"type\":\"object\"}"),
            CreateLocalToolDto(WebFetchToolDefinition.ToolName, "{\"type\":\"object\"}"),
            CreateLocalToolDto(WebSearchToolDefinition.ToolName, "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
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
                           UseLocalTools: true,
                           SuppressOperatorTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(expected: 2, runner.LastAllowedTools.Count);
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == "GetCurrentTime");
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == "Calculate");
        AssertEx.False(runner.LastAllowedTools.Any(tool => tool.Name == AskUserTool.ToolName),
            "The suppressed turn must reach the runtime package without ask_user.");
        AssertEx.False(runner.LastAllowedTools.Any(tool => WebAccessToolCatalog.IsWebTool(tool.Name)),
            "A web result would park on a review card nobody can open, so neither web tool may reach the package.");
    }

    [Test]
    public async Task SendMessageAsync_WhenOperatorToolsAreNotSuppressed_LeavesAskUserAndTheWebToolsInPlace()
    {
        // The other half of the pin: suppression is opt-in per send, so an interactive turn with web access on keeps
        // every tool it was offered. Same offer as the suppressed case, so only the flag can explain the difference.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"),
            CreateLocalToolDto(AskUserTool.ToolName, "{\"type\":\"object\"}"),
            CreateLocalToolDto(WebFetchToolDefinition.ToolName, "{\"type\":\"object\"}"),
            CreateLocalToolDto(WebSearchToolDefinition.ToolName, "{\"type\":\"object\"}"),
            CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
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
        AssertEx.Equal(expected: 5, runner.LastAllowedTools.Count);
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == AskUserTool.ToolName);
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == WebFetchToolDefinition.ToolName);
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == WebSearchToolDefinition.ToolName);
    }

    [Test]
    public async Task SendMessageAsync_WhenResolvedNull_NodePolicyTightensFallbackOffer()
    {
        // An unbound or deleted-agent turn (resolved == null) builds its offer on the fallback path, which still runs the
        // node approval policy: with approval required for ReadLocal, the fallback ReadLocal tool requires approval.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}", ToolCategory.ReadLocal));
        var nodePolicy = new NodeToolApprovalPolicy(new Dictionary<ToolCategory, bool>
            {
                [ToolCategory.ReadLocal] = true
            },
            new Dictionary<string, bool>(StringComparer.Ordinal));
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
            CreateLocalDefaultChatModelResolver(),
            CreateMemoryExtractionDispatcher(),
            Substitute.For<IConversationMaintenanceDispatcher>(),
            CreateTurnContextBuilder(),
            Substitute.For<IConversationSandboxStager>(),
            Options.Create(new ChatStreamBudgetOptions()),
            TimeProvider.System,
            nodePolicy,
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
        var readLocalTool = runner.LastAllowedTools.Single(tool => tool.Name == "GetCurrentTime");
        AssertEx.Equal(expected: true, readLocalTool.RequiresApproval);
    }

    /// <summary>
    ///     A definition widened between the development-workflow lane's early binding check and the send: the resolver
    ///     answers narrow once, then write-capable, and the turn is refused on the offer it would hand the model.
    /// </summary>
    [Test]
    public async Task SendMessageAsync_WhenTheDefinitionWidensAfterTheEarlyCheck_RefusesTheTurnOnTheOfferItWouldRun()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"));
        var resolver = CreateWideningAgentDefinitionResolver(agentDefinitionId);
        var service = CreateWriteDeclarationService(persistence, runner, dispatcher, resolver, offerProvider);

        // The early answer, taken while the definition is still narrow — the pre-session check the workflow lane makes.
        var early = await new WorkSessionWriteDeclarationGuard(resolver, offerProvider, CreateNodeSettingsStore(), CreateLocalDefaultChatModelResolver())
            .InspectAsync(agentDefinitionId, WriteDeclarationModel, CancellationToken.None);
        AssertEx.Null(early, "The definition carries nothing that writes when the early check reads it.");

        var refusal = await AssertEx.ThrowsAsync<WorkSessionUndeclaredWriteException>(async () =>
        {
            await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                               "hello",
                               MessageId: assistantMessageId,
                               RequestId: requestId,
                               Model: WriteDeclarationModel,
                               UseLocalTools: true,
                               AgentDefinitionId: agentDefinitionId,
                               RefuseUndeclaredWrites: true)))
            {
                // The refusal lands before the first tool-bearing event; nothing here is expected to run.
            }
        });

        AssertEx.Contains(refusal.Message, WriteDeclarationToolName, StringComparison.Ordinal);
        AssertEx.Contains(refusal.Message, "GRAPH-C4-2", StringComparison.Ordinal);
        AssertEx.Empty(runner.LastAllowedTools, "The refused turn never reached the runner, so no offer was ever handed to a model.");
    }

    /// <summary>
    ///     The same widened definition on a turn that never armed the rule (an ordinary chat send or an undriven work
    ///     session): the write tool is offered and the turn is sent.
    /// </summary>
    [Test]
    public async Task SendMessageAsync_WhenTheWriteDeclarationIsNotArmed_SendsTheWriteCapableOfferUnchanged()
    {
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("GetCurrentTime", "{\"type\":\"object\"}"));
        var resolver = CreateWideningAgentDefinitionResolver(agentDefinitionId);
        var service = CreateWriteDeclarationService(persistence, runner, dispatcher, resolver, offerProvider);

        // Burn the narrow first answer so this send resolves the widened definition, exactly as the refusing test does.
        _ = await resolver.ResolveAsync(agentDefinitionId, WriteDeclarationModel, retrievalQuery: null, supportsTools: true, honorModelProfile: false,
            activeModelIsCloud: false, CancellationToken.None);

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: WriteDeclarationModel,
                           UseLocalTools: true,
                           AgentDefinitionId: agentDefinitionId)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Contains(runner.LastAllowedTools, tool => tool.Name == WriteDeclarationToolName);
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
                           UseLocalTools: false)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Empty(runner.LastAllowedTools);
    }

    [Test]
    public async Task SendMessageAsync_WhenLocalToolsDisabled_StillOffersAnAgentsOwnToolList()
    {
        // The seeded Mathematician names run_python; with the composer toggle off the model must still get it as a tool,
        // or it writes the call as text.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var own = CreateLocalToolDto("run_python", "{\"type\":\"object\"}");
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(new ResolvedAgentRuntime("Persona.", [own], WriteDeclarationModel, null, 1, agentDefinitionId, "Mathematician", DeclaresToolList: true));
        var service = CreateWriteDeclarationService(persistence, runner, dispatcher, resolver, CreateOfferProvider(own, CreateLocalToolDto("Calculate", "{\"type\":\"object\"}")));

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: WriteDeclarationModel,
                           UseLocalTools: false,
                           AgentDefinitionId: agentDefinitionId)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal("run_python", string.Join(",", runner.LastAllowedTools.Select(static tool => tool.Name)));
    }

    [Test]
    public async Task SendMessageAsync_WhenGgufModelIsToolCapable_OffersTools()
    {
        // A llama.cpp (non-Ollama) model whose chat template was detected tool-capable must be offered tools — the GGUF
        // capability resolver supplies the caps, NOT an /api/show probe (which would fail / stall in desktop mode).
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns("llamacpp");
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(providerResolver: providerResolver,
                    gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: true, SupportsTools: true, SupportsVision: false))),
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
                           UseLocalTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        AssertEx.Equal(expected: 1, runner.LastAllowedTools.Count);
        AssertEx.Equal("Calculate", runner.LastAllowedTools[0].Name);
    }

    [Test]
    [Arguments(true, true, false, true)]
    [Arguments(false, true, false, false)]
    [Arguments(true, false, false, false)]
    [Arguments(false, true, true, true)]
    public async Task SendMessageAsync_WhenGgufModelIsNotToolCapable_OffersNoToolsAndSaysSoOnlyWhenToolsWereAsked(bool useLocalTools,
        bool nodeToolsEnabled,
        bool agentDeclaresToolList,
        bool expectNotice)
    {
        // A tool-less llama.cpp template gets no tools. ToolsWithheld fires only when the turn asked (the toggle or an
        // agent's own list) AND the node engine is on.
        var agentResolver = CreateAgentDefinitionResolver();
        if (agentDeclaresToolList)
        {
            agentResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                         .Returns(new ResolvedAgentRuntime("Persona.", [], null, null, 1, Guid.NewGuid(), "Mathematician", DeclaresToolList: true));
        }

        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns("llamacpp");
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentResolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(providerResolver: providerResolver,
                    gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: false, SupportsVision: false))),
                NullLogger<ChatTurnResolver>.Instance),
            new NodeChatMutationGuard(persistence),
            new LocalChatRuntimePackageBuilder(),
            runner,
            dispatcher,
            Options.Create(new LocalChatAgentOptions
            {
                EnableTools = true
            }),
            StubNodeRuntimeSettings.Create().WithEnableTools(nodeToolsEnabled).Build(),
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

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: useLocalTools)))
        {
            events.Add(streamEvent);
        }

        AssertEx.NotEmpty(events);
        AssertEx.Empty(runner.LastAllowedTools);
        AssertEx.Equal(expectNotice ? 1 : 0,
            events.Count(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice && streamEvent.NoticeKind == nameof(TurnNoticeKind.ToolsWithheld)));
    }

    [Test]
    [Arguments(false, 1)]
    [Arguments(true, 0)]
    public async Task SendMessageAsync_WhenATemplateToolCapableModelIsNotAllowListed_SaysTheSettingTrimmedTheOffer(bool allowListed, int expectedNotices)
    {
        // The template gate passes, but the allow-list gate (not self-healed) trims the offer to the basic tools. That
        // is never silent: it raises the same ToolsWithheld kind with a message naming the setting.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        offerProvider.IsToolCapable(Arg.Any<string?>()).Returns(allowListed);
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns("llamacpp");
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(providerResolver: providerResolver,
                    gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: true, SupportsVision: false))),
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

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)))
        {
            events.Add(streamEvent);
        }

        var notices = events.Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                         && streamEvent.NoticeKind == nameof(TurnNoticeKind.ToolsWithheld))
                            .ToList();
        AssertEx.Equal(expectedNotices, notices.Count);
        AssertEx.NotEmpty(runner.LastAllowedTools);
        AssertEx.True(notices.TrueForAll(static notice => notice.NoticeMessage?.Contains("Tool-capable models", StringComparison.Ordinal) == true),
            "the allow-list notice names the setting the operator has to change");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendMessageAsync_WhenAToolTurnLostToolsToTheCloudSwitches_SaysSoOnceWithTheSwitchCodes(bool providerReportsWithheld)
    {
        // Send/regenerate parity for CloudToolsWithheld. An unbound turn is plain chat, which never offers spawn_subagent,
        // so its switch drops out of Detail.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        offerProvider.GetCloudWithheldToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<CloudWithheldTool>>(providerReportsWithheld
                         ?
                         [
                             new CloudWithheldTool
                             {
                                 Name = "spawn_subagent",
                                 Switch = CloudToolSwitch.SubAgents
                             },
                             new CloudWithheldTool
                             {
                                 Name = "web_fetch",
                                 Switch = CloudToolSwitch.WebTools
                             },
                             new CloudWithheldTool
                             {
                                 Name = "mcp__weather__get_forecast",
                                 Switch = CloudToolSwitch.McpTools
                             }
                         ]
                         : []));
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns("llamacpp");
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(providerResolver: providerResolver,
                    gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: true, SupportsVision: false))),
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

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           UseLocalTools: true)))
        {
            events.Add(streamEvent);
        }

        AssertEx.NotEmpty(runner.LastAllowedTools);
        // One entry per notice, so this pins both "exactly one" and its Detail.
        var noticeDetails = events.Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                               && streamEvent.NoticeKind == nameof(TurnNoticeKind.CloudToolsWithheld))
                                  .Select(static streamEvent => $"[{streamEvent.NoticeDetail}]");
        AssertEx.Equal(providerReportsWithheld ? "[mcp-tools, web-tools]" : "", string.Concat(noticeDetails));
        if (providerReportsWithheld)
        {
            AssertEx.Contains(events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.CloudToolsWithheld)).NoticeMessage,
                "grant them on the model's external connection");
        }
    }
}
