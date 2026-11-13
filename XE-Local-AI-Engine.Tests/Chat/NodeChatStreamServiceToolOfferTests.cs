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
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.WebAccess;
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
    public async Task SendMessageAsync_LeavesAskUserAndTheWebToolsInPlace()
    {
        // An interactive turn with web access on keeps every tool it was offered, including the ones that park on its
        // operator: ask_user and both web tools.
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
                .Returns(new ResolvedAgentRuntime("Persona.", [own], ToolCapableModel, null, 1, agentDefinitionId, "Mathematician", DeclaresToolList: true));
        var service = CreateServiceWithResolver(persistence, runner, dispatcher, resolver, CreateOfferProvider(own, CreateLocalToolDto("Calculate", "{\"type\":\"object\"}")));

        var drained = 0;
        await foreach (var _ in service.SendMessageAsync(new NodeChatStreamRequest(conversationId,
                           "hello",
                           MessageId: assistantMessageId,
                           RequestId: requestId,
                           Model: ToolCapableModel,
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
        // A llama.cpp model whose chat template was detected tool-capable must be offered tools — the GGUF capability
        // resolver supplies the caps.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { });
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ReasoningCapturingInvocationRunner(dispatcher);
        var offerProvider = CreateOfferProvider(CreateLocalToolDto("Calculate", "{\"type\":\"object\"}"));
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: true, SupportsTools: true, SupportsVision: false))),
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
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentResolver, CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: false, SupportsVision: false))),
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
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: true, SupportsVision: false))),
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
        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(CreateAgentDefinitionResolver(), CreateAgentDefinitionStore(), CreateOrchestrationResolver(),
                CreateModelCapabilityResolver(gguf: CreateGgufModelCapabilityResolver(new GgufModelCapabilities(SupportsThinking: false, SupportsTools: true, SupportsVision: false))),
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
