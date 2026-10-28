namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using static XE_Local_AI_Engine.Tests.Chat.NodeChatStreamServiceTestSupport;

[Category(TestCategories.Unit)]
public sealed class NodeChatStreamServiceOrchestrationTests
{
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
        // The chat-turn resolver now gates the orchestration reload on the resolved runtime's Kind (it reuses the
        // definition the resolver already loaded), so the resolver must surface Kind=Orchestrator for this bound agent.
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Orchestrator persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 4,
                                   agentDefinitionId, "Orchestrator", Kind: AgentDefinitionKind.Orchestrator));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        var spec = CreateSampleSpec();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(OrchestrationResolution.Compiled(new ResolvedOrchestration
                             {
                                 Spec = spec,
                                 ResolvedSystemPrompt = "Orchestrator prompt.",
                                 ModelProfile = "qwen3:8b",
                                 ReasoningEffort = null,
                                 AgentDefinitionVersion = 4,
                                 AnyParticipantNeedsCloudDataSwitch = false,
                                 FirstUngrantedCloudParticipantModel = null
                             }));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentDefinitionResolver, store, orchestrationResolver, CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
        AssertEx.NotNull(runner.LastOrchestrationSpec);
        AssertEx.Equal(spec.TriageParticipantKey, runner.LastOrchestrationSpec!.TriageParticipantKey);
        AssertEx.Equal(expected: 2, runner.LastOrchestrationSpec.Participants.Count);
    }

    [Test]
    public async Task SendMessageAsync_WhenOperatorToolsAreSuppressed_WithholdsThemFromEveryOrchestrationParticipant()
    {
        // Participants carry their own projected offer on the compiled spec, so the withdrawal has to reach it too. This
        // send offers no local tools, which leaves the spec as the only path the assertion can be reading.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new PackageCapturingInvocationRunner(dispatcher);

        var store = Substitute.For<IAgentDefinitionStore>();
        store.GetByIdAsync(agentDefinitionId, Arg.Any<CancellationToken>()).Returns(CreateOrchestratorRecord(agentDefinitionId));
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Orchestrator persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 4,
                                   agentDefinitionId, "Orchestrator", Kind: AgentDefinitionKind.Orchestrator));
        var orchestrationResolver = Substitute.For<IOrchestrationResolver>();
        var spec = CreateSampleSpec();
        orchestrationResolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns(OrchestrationResolution.Compiled(new ResolvedOrchestration
                             {
                                 Spec = spec,
                                 ResolvedSystemPrompt = "Orchestrator prompt.",
                                 ModelProfile = "qwen3:8b",
                                 ReasoningEffort = null,
                                 AgentDefinitionVersion = 4,
                                 AnyParticipantNeedsCloudDataSwitch = false,
                                 FirstUngrantedCloudParticipantModel = null
                             }));

        var service = new NodeChatStreamService(persistence,
            new ChatInvocationStatePump(ChatPumpTestFactory.Create(persistence), TimeProvider.System),
            new ChatTurnResolver(agentDefinitionResolver, store, orchestrationResolver, CreateModelCapabilityResolver(), NullLogger<ChatTurnResolver>.Instance),
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
                           SuppressOperatorTools: true)))
        {
            drained++;
        }

        AssertEx.True(drained > 0, "Expected the send to stream events.");
        var withdrawn = AssertEx.NotNull(runner.LastOrchestrationSpec);
        AssertEx.Equal(expected: 2, withdrawn.Participants.Count, "The withdrawal rewrites the tools and nothing else.");
        AssertEx.False(withdrawn.Participants.Any(participant => participant.Tools.Any(tool => tool.Name == AskUserTool.ToolName)),
            "No participant may reach the runner still able to park on a question.");
        AssertEx.False(withdrawn.Participants.Any(participant => participant.Tools.Any(tool => WebAccessToolCatalog.IsWebTool(tool.Name))),
            "No participant may reach the runner still able to park on a web review.");
        AssertEx.Contains(withdrawn.Participants.Single(participant => participant.Key == "b").Tools, tool => tool.Name == "Calculate");
        AssertEx.Contains(spec.Participants, participant => participant.Tools.Any(tool => tool.Name == AskUserTool.ToolName));
    }

    [Test]
    public async Task SendMessageAsync_WhenOrchestrationDegrades_EmitsOneNoticeNamingTheReason()
    {
        // An orchestrator whose orchestration does not compile runs as a lone single agent. A server log alone would
        // leave the operator with an ordinary answer and no hint the team never ran, so the turn says so.
        var events = await RunOrchestrationDegradeAsync(AgentDefinitionKind.Orchestrator,
            OrchestrationResolution.Degraded(OrchestrationDegradationReason.TooFewCapableParticipants,
                "only 1 of its agents can call tools, and at least 2 are required"));

        AssertEx.ContainsSingle(events,
            streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice && streamEvent.NoticeKind == nameof(TurnNoticeKind.OrchestrationDegraded),
            "a degraded orchestration must raise exactly one operator notice");
        var notice = events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.OrchestrationDegraded));
        AssertEx.Contains(notice.NoticeMessage, "only 1 of its agents can call tools");
        AssertEx.Contains(notice.NoticeMessage, "ran as a single agent");
    }

    [Test]
    public async Task SendMessageAsync_WhenAgentIsSingleKind_EmitsNoOrchestrationDegradedNotice()
    {
        // A Single-kind agent never asked for orchestration, so it must stay silent even with an orchestration resolver
        // primed to degrade — the chat-turn resolver short-circuits before it is ever consulted.
        var events = await RunOrchestrationDegradeAsync(AgentDefinitionKind.Single,
            OrchestrationResolution.Degraded(OrchestrationDegradationReason.TopologyInvalid, "its handoff topology is missing or invalid"));

        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.OrchestrationDegraded)),
            "a single-agent definition must never raise the orchestration-degraded notice");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendMessageAsync_EmitsTheComputeWithheldNotice_OnlyWhenTheResolverWithheldRunPython(bool computeWithheld)
    {
        // An agent listing run_python on a node with compute off loses the tool with a notice, never silently.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(new ResolvedAgentRuntime("Agent persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentDefinitionId, "Agent",
                                   ComputeWithheld: computeWithheld));
        var service = CreateServiceWithScopeFactory(persistence,
            new ContextCapturingInvocationRunner(dispatcher),
            dispatcher,
            CreateScopeFactory(),
            allowCloudModelAccess: false,
            agentDefinitionResolver,
            Substitute.For<IAgentDefinitionStore>(),
            Substitute.For<IOrchestrationResolver>());

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId, "hello", MessageId: assistantMessageId, RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        var notices = events.Where(static streamEvent => streamEvent.NoticeMessage == ChatTurnResolution.ComputeWithheldNoticeMessage).ToList();
        AssertEx.Equal(computeWithheld ? 1 : 0, notices.Count);
        if (computeWithheld)
        {
            AssertEx.Equal(nameof(TurnNoticeKind.ToolsWithheld), notices[0].NoticeKind);
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendMessageAsync_EmitsOnePlaybookWithheldNotice_OnlyWhenTheResolverWithheldMemory(bool playbookWithheld)
    {
        // The resolver owns the gate (cloud model, flag off, enabled memory present); the send only surfaces its verdict,
        // naming the effective model as KnowledgeWithheld does.
        var events = await RunOrchestrationDegradeAsync(AgentDefinitionKind.Single, OrchestrationResolution.NotOrchestrated, playbookWithheld, modelProfile: "qwen3:8b");

        var notices = events.Where(streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                  && streamEvent.NoticeKind == nameof(TurnNoticeKind.PlaybookWithheld))
                            .ToList();
        AssertEx.Equal(playbookWithheld ? 1 : 0, notices.Count);
        if (playbookWithheld)
        {
            AssertEx.Equal("qwen3:8b", notices[0].NoticeDetail);
            AssertEx.Contains(notices[0].NoticeMessage, "learned playbook was not applied");
            AssertEx.Contains(notices[0].NoticeMessage, "grant local data on the model's external connection");
        }
    }

    [Test]
    public async Task SendMessageAsync_WhenAPerSendPickIsGone_DoesNotSayTheConversationsAgentIsGone()
    {
        // The notice says "the agent this conversation uses": a deleted per-send pick is not that agent.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, Guid.NewGuid());
        var dispatcher = new RecordingWorkerEventDispatcher();
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns((ResolvedAgentRuntime?)null);
        var service = CreateServiceWithScopeFactory(persistence,
            new ContextCapturingInvocationRunner(dispatcher),
            dispatcher,
            CreateScopeFactory(),
            allowCloudModelAccess: false,
            agentDefinitionResolver,
            Substitute.For<IAgentDefinitionStore>(),
            Substitute.For<IOrchestrationResolver>());

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId, "hello", MessageId: assistantMessageId, RequestId: requestId,
                           AgentDefinitionId: Guid.NewGuid())))
        {
            events.Add(streamEvent);
        }

        AssertEx.False(events.Any(static streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AgentUnavailable)),
            "only the conversation's own binding earns the agent-unavailable wording");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SendMessageAsync_WhenTheBoundAgentDefinitionIsGone_EmitsOneAgentUnavailableNotice(bool definitionDeleted)
    {
        // A conversation bound to a deleted definition says so, never silently falling back to the default assistant.
        var conversationId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var agentDefinitionId = Guid.NewGuid();
        var persistence = CreatePersistence(conversationId, assistantMessageId, requestId, _ => { }, agentDefinitionId);
        var dispatcher = new RecordingWorkerEventDispatcher();
        var runner = new ContextCapturingInvocationRunner(dispatcher);

        var store = Substitute.For<IAgentDefinitionStore>();
        var agentDefinitionResolver = Substitute.For<IAgentDefinitionResolver>();
        agentDefinitionResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                               .Returns(definitionDeleted
                                   ? null
                                   : new ResolvedAgentRuntime("Agent persona.", [], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentDefinitionId, "Agent"));
        var service = CreateServiceWithScopeFactory(persistence,
            runner,
            dispatcher,
            CreateScopeFactory(),
            allowCloudModelAccess: false,
            agentDefinitionResolver,
            store,
            Substitute.For<IOrchestrationResolver>());

        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in service.SendMessageAsync(new NodeChatStreamRequest(conversationId, "hello", MessageId: assistantMessageId, RequestId: requestId)))
        {
            events.Add(streamEvent);
        }

        var notices = events.Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                         && streamEvent.NoticeKind == nameof(TurnNoticeKind.AgentUnavailable))
                            .ToList();
        AssertEx.Equal(definitionDeleted ? 1 : 0, notices.Count);
        if (definitionDeleted)
        {
            AssertEx.Equal(agentDefinitionId.ToString(), notices[0].NoticeDetail);
            AssertEx.Equal("The agent this conversation uses no longer exists, so the default assistant answered.", notices[0].NoticeMessage);
        }
    }

    [Test]
    public async Task SendMessageAsync_WhenOrchestrationWithheldParticipantPlaybooks_EmitsOneNoticeListingThem()
    {
        // A compiled orchestration runs its participants' prompts, so the notice lists the withheld participants — and
        // ignores the orchestrator's own flag, whose persona the orchestration never runs.
        var events = await RunOrchestrationDegradeAsync(AgentDefinitionKind.Orchestrator,
            OrchestrationResolution.Compiled(new ResolvedOrchestration
            {
                Spec = CreateSampleSpec(),
                ResolvedSystemPrompt = "Orchestrator prompt.",
                ModelProfile = null,
                ReasoningEffort = null,
                AgentDefinitionVersion = 4,
                AnyParticipantNeedsCloudDataSwitch = true,
                FirstUngrantedCloudParticipantModel = "azure-specialist-deploy",
                PlaybookWithheldParticipantNames = ["Researcher", "Writer"]
            }),
            playbookWithheld: true);

        AssertEx.ContainsSingle(events,
            streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice && streamEvent.NoticeKind == nameof(TurnNoticeKind.PlaybookWithheld),
            "an orchestration raises at most one playbook-withheld notice");
        AssertEx.Equal("Researcher, Writer", events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.PlaybookWithheld)).NoticeDetail);
        AssertEx.Contains(events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.PlaybookWithheld)).NoticeMessage,
            "grant local data on each participant's external connection");
    }

    [Test]
    public async Task SendMessageAsync_WhenOrchestrationParticipantsLostToolsToTheCloudSwitches_EmitsOneNoticeEvenWithoutAToolRequest()
    {
        // The participants carry their own tools whatever the client asked (this send asks for none), and the
        // orchestrator's own list is ignored: its persona never runs in a compiled orchestration.
        var events = await RunOrchestrationDegradeAsync(AgentDefinitionKind.Orchestrator,
            OrchestrationResolution.Compiled(new ResolvedOrchestration
            {
                Spec = CreateSampleSpec(),
                ResolvedSystemPrompt = "Orchestrator prompt.",
                ModelProfile = null,
                ReasoningEffort = null,
                AgentDefinitionVersion = 4,
                AnyParticipantNeedsCloudDataSwitch = true,
                FirstUngrantedCloudParticipantModel = "azure-specialist-deploy",
                CloudWithheldTools =
                [
                    new CloudWithheldTool
                    {
                        Name = "mcp__weather__get_forecast",
                        Switch = CloudToolSwitch.McpTools
                    }
                ]
            }),
            resolvedCloudWithheldTools:
            [
                new CloudWithheldTool
                {
                    Name = "spawn_subagent",
                    Switch = CloudToolSwitch.SubAgents
                }
            ]);

        // One entry per notice, so this pins both "exactly one" and its Detail.
        var noticeDetails = events.Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantNotice
                                                               && streamEvent.NoticeKind == nameof(TurnNoticeKind.CloudToolsWithheld))
                                  .Select(static streamEvent => $"[{streamEvent.NoticeDetail}]");
        AssertEx.Equal("[mcp-tools]", string.Concat(noticeDetails));
        AssertEx.Contains(events.First(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.CloudToolsWithheld)).NoticeMessage,
            "grant them on each participant's external connection");
    }

    [Test]
    public async Task SendMessageAsync_WhenOrchestrationHasUngrantedCloudParticipant_LocalRoot_WithholdsSharedSeedAttachment()
    {
        // The orchestration seed is ONE list shared by every participant, so a LOCAL root with an ungranted CLOUD
        // participant withholds node-local attachment content without opt-in: tool stripping cannot redact the seed.
        var (events, capturedContext) = await RunOrchestrationAttachmentEgressAsync(anyParticipantNeedsCloudDataSwitch: true, allowCloudModelAccess: false);

        AssertEx.False(capturedContext.Any(message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal)),
            "the shared orchestration seed must not carry attachment content when a participant is cloud and there is no opt-in");
        AssertEx.False(capturedContext.Any(message => message.Content.Contains(ConversationAttachmentContextComposer.Preamble, StringComparison.Ordinal)),
            "the attachment context block must not be composed for a mixed-locality orchestration without opt-in");
        var notice = events.Single(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld));
        AssertEx.Equal("azure-specialist-deploy", notice.NoticeDetail, "the notice names the ungranted cloud participant, not the local root");
    }

    [Test]
    public async Task SendMessageAsync_WhenOrchestrationHasUngrantedCloudParticipant_AndOperatorOptedIn_ComposesAttachment()
    {
        // Opt-in restores the shared seed: with AllowCloudModelAccess the mixed-locality orchestration inlines the
        // attachment exactly as an all-local turn would.
        var (_, capturedContext) = await RunOrchestrationAttachmentEgressAsync(anyParticipantNeedsCloudDataSwitch: true, allowCloudModelAccess: true);

        AssertEx.Contains(capturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
    }

    [Test]
    public async Task SendMessageAsync_WhenNoOrchestrationParticipantNeedsTheCloudDataSwitch_ComposesAttachmentAndDoesNotNotify()
    {
        // All-local, or every cloud participant's connection grants local data (the resolver folds both into the
        // aggregate): the attachment composes with AllowCloudModelAccess off and no withheld notice fires.
        var (events, capturedContext) = await RunOrchestrationAttachmentEgressAsync(anyParticipantNeedsCloudDataSwitch: false, allowCloudModelAccess: false);

        AssertEx.Contains(capturedContext, message => message.Content.Contains("The launch code is alpha-zero.", StringComparison.Ordinal));
        AssertEx.False(events.Any(streamEvent => streamEvent.NoticeKind == nameof(TurnNoticeKind.AttachmentsWithheld)),
            "an orchestration with no ungranted cloud participant must not trigger the attachments-withheld notice");
    }
}
