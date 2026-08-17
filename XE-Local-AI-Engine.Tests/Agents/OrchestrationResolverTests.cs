namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class OrchestrationResolverTests
{
    private const string ToolCapableModel = "qwen3:8b";
    private const string IncapableModel = "tiny:1b";
    private const string CapabilityGatedToolName = "run_in_agent_home";

    [Test]
    public async Task ResolveAsync_WhenKindIsSingle_ReturnsNull()
    {
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        var single = CreateDefinition(kind: AgentDefinitionKind.Single, modelProfile: ToolCapableModel);

        var resolution = await resolver.ResolveAsync(single, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolution.Orchestration is null, "A single-agent definition must never resolve to an orchestration.");
        // NOT a degradation: a Single-kind agent never asked for orchestration, so it must raise no operator notice.
        AssertEx.Equal(OrchestrationDegradationReason.None, resolution.Reason);
        AssertEx.Null(resolution.DegradationNotice);
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenNoTopology_ReturnsNull()
    {
        var resolver = CreateResolver(out _, OfferTool("GetCurrentTime"));
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: null);

        var resolution = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolution.Orchestration is null, "An orchestrator with no topology must degrade to single-agent (null).");
        AssertEx.Equal(OrchestrationDegradationReason.TopologyInvalid, resolution.Reason);
        AssertEx.NotNull(resolution.DegradationNotice);
    }

    [Test]
    public async Task ResolveAsync_WhenInvalidTopology_ReturnsNull()
    {
        var resolver = CreateResolver(out _, OfferTool("GetCurrentTime"));
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: "{ not json");

        var resolution = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolution.Orchestration is null, "An invalid topology must degrade to single-agent (null).");
        AssertEx.Equal(OrchestrationDegradationReason.TopologyInvalid, resolution.Reason);
    }

    [Test]
    public async Task ResolveAsync_WhenEffectiveModelNotToolCapable_ReturnsNull()
    {
        var triage = CreateDefinition(kind: AgentDefinitionKind.Single, modelProfile: ToolCapableModel);
        var specialist = CreateDefinition(kind: AgentDefinitionKind.Single, modelProfile: ToolCapableModel);
        var orchestrator = CreateOrchestrator(IncapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolution = await resolver.ResolveAsync(orchestrator, IncapableModel).ConfigureAwait(false);

        AssertEx.True(resolution.Orchestration is null, "An incapable orchestrator model must degrade the whole orchestration to single-agent.");
        AssertEx.Equal(OrchestrationDegradationReason.ModelNotToolCapable, resolution.Reason);
    }

    [Test]
    public async Task ResolveAsync_WhenTriageMissing_ReturnsNull()
    {
        var specialistA = CreateDefinition(modelProfile: ToolCapableModel);
        var specialistB = CreateDefinition(modelProfile: ToolCapableModel);
        var triageId = Guid.NewGuid();
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triageId, // points at a definition that does not exist
            ParticipantAgentDefinitionIds = [triageId, specialistA.Id, specialistB.Id]
        };
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: OrchestrationTopologyJson.Serialize(topology));
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, specialistA, specialistB);
        store.GetByIdAsync(triageId, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var resolution = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolution.Orchestration is null, "A topology whose triage no longer exists must degrade to single-agent.");
        AssertEx.Equal(OrchestrationDegradationReason.TriageMissing, resolution.Reason);
    }

    [Test]
    public async Task ResolveAsync_WhenFewerThanTwoCapableParticipants_ReturnsNull()
    {
        // Two participants listed, but one pins an incapable model and is dropped, leaving only the triage.
        var triage = CreateDefinition(modelProfile: ToolCapableModel);
        var incapableSpecialist = CreateDefinition(modelProfile: IncapableModel);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, incapableSpecialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, incapableSpecialist);

        var resolution = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolution.Orchestration is null, "Fewer than two capable participants must degrade to single-agent.");
        AssertEx.Equal(OrchestrationDegradationReason.TooFewCapableParticipants, resolution.Reason);
        // The count is named so the operator knows how far short the topology fell, not just that it did.
        AssertEx.Contains(resolution.DegradationNotice ?? string.Empty, "only 1 of its agents can call tools");
    }

    [Test]
    public async Task ResolveAsync_WhenValid_CompilesSpecWithTriageAndParticipants()
    {
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        AssertEx.Equal(triage.Id.ToString("D"), resolved!.Spec.TriageParticipantKey);
        AssertEx.Equal(expected: 2, resolved.Spec.Participants.Count);
        AssertEx.Contains(resolved.Spec.Participants, participant => participant.Key == specialist.Id.ToString("D"));
        // The orchestrator's own single-agent inputs ride alongside the spec for the degrade-safe fallback.
        AssertEx.Equal(orchestrator.Instructions, resolved.ResolvedSystemPrompt);
        AssertEx.Equal(orchestrator.Version, resolved.AgentDefinitionVersion);
    }

    [Test]
    public async Task ResolveAsync_ResolvesEachParticipantThinkingFromItsOwnEffectiveModel()
    {
        // Two tool-capable participants pinned to DIFFERENT models: one thinking-capable, one not. Each participant's
        // SupportsThinking must come from its OWN effective model, not the turn model's — so a graded reasoning effort
        // on a pinned non-thinking model can never reach the think wire.
        const string thinkingModel = "qwen3:8b";
        const string nonThinkingModel = "gemma:9b";
        var triage = CreateDefinition("Triage", modelProfile: thinkingModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: nonThinkingModel, allowedTools: ["GetCurrentTime"]);
        var orchestrator = CreateOrchestrator(thinkingModel, triage, [triage, specialist]);

        var store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new[]
        {
            OfferTool("GetCurrentTime")
        });
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithToolCapableModels(thinkingModel, nonThinkingModel).Build();

        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(thinkingModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false)));
        capabilityResolver.ResolveAsync(nonThinkingModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: true, IsCloud: false)));

        var resolver = new OrchestrationResolver(store,
            playbookStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            runtimeSettings,
            capabilityResolver,
            new FakeAgentInstructionProvider(),
            new PermissiveToolApprovalPolicy(),
            NullLogger<OrchestrationResolver>.Instance);
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, thinkingModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var triageSpec = resolved!.Spec.Participants.Single(participant => participant.Key == triage.Id.ToString("D"));
        var specialistSpec = resolved.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.True(triageSpec.SupportsThinking, "a participant pinned to a thinking-capable model must resolve SupportsThinking=true");
        AssertEx.False(specialistSpec.SupportsThinking, "a participant pinned to a non-thinking model must resolve SupportsThinking=false even when the turn model can think");
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantPinnedToCloudModel_OnLocalActiveTurn_WithholdsKnowledgeTools()
    {
        // Effective-model knowledge-tool locality gate: the turn's active model is LOCAL, but a participant pins a
        // CLOUD model. That participant's knowledge tools must be gated on ITS OWN effective model's locality, resolved
        // per participant.
        var resolved = await ResolveWithCloudPinnedParticipantAsync(allowCloudKnowledgeAccess: false).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var localTriage = resolved!.Spec.Participants.Single(participant => participant.Name == "Triage");
        var cloudSpecialist = resolved.Spec.Participants.Single(participant => participant.Name == "Specialist");
        AssertEx.Contains(localTriage.Tools, tool => tool.Name == KnowledgeSearchToolName);
        AssertEx.False(cloudSpecialist.Tools.Any(tool => tool.Name == KnowledgeSearchToolName),
            "a cloud-pinned participant must not be offered the knowledge tools even on a local-active turn");
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantPinnedToCloudModel_AndOperatorOptedIn_OffersKnowledgeTools()
    {
        var resolved = await ResolveWithCloudPinnedParticipantAsync(allowCloudKnowledgeAccess: true).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var cloudSpecialist = resolved!.Spec.Participants.Single(participant => participant.Name == "Specialist");
        AssertEx.Contains(cloudSpecialist.Tools, tool => tool.Name == KnowledgeSearchToolName);
    }

    [Test]
    public async Task ResolveAsync_WhenAnyParticipantIsCloud_SurfacesAggregateAndNamesCloudModel()
    {
        // Blocker 1: the caller gates the SHARED orchestration seed's attachment content on this aggregate (a cloud
        // participant taints the whole shared seed), and names the cloud model in the withheld notice.
        var resolved = await ResolveWithCloudPinnedParticipantAsync(allowCloudKnowledgeAccess: false).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.True(resolved!.AnyParticipantIsCloud, "a cloud-pinned participant must make the aggregate cloud-reaching");
        AssertEx.Equal(CloudParticipantModel, resolved.FirstCloudParticipantModel);
    }

    [Test]
    public async Task ResolveAsync_WhenAllParticipantsLocal_AggregateIsNotCloud()
    {
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        AssertEx.False(resolved!.AnyParticipantIsCloud, "an all-local orchestration must not be flagged cloud-reaching");
        AssertEx.Null(resolved.FirstCloudParticipantModel);
    }

    private const string KnowledgeSearchToolName = "search_knowledge_base";
    private const string CloudParticipantModel = "azure-foundry-deploy";

    private static async Task<ResolvedOrchestration?> ResolveWithCloudPinnedParticipantAsync(bool allowCloudKnowledgeAccess)
    {
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: [KnowledgeSearchToolName]);
        var specialist = CreateDefinition("Specialist", modelProfile: CloudParticipantModel, allowedTools: [KnowledgeSearchToolName]);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);

        var store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));

        // REAL offer provider so the actual withholding is observed. BOTH participant models are tool-capable, so the
        // knowledge tools WOULD be offered but for the per-participant locality gate.
        var offerProvider = new LocalToolOfferProvider(new LocalAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels(ToolCapableModel, CloudParticipantModel).Build(),
            NullCustomToolScopeFactory.Instance,
            allowCloudKnowledgeAccess);
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithToolCapableModels(ToolCapableModel, CloudParticipantModel).Build();

        var capabilityResolver = Substitute.For<IModelCapabilityResolver>();
        capabilityResolver.ResolveAsync(ToolCapableModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false)));
        capabilityResolver.ResolveAsync(CloudParticipantModel, Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: true, IsCloud: true)));

        var resolver = new OrchestrationResolver(store,
            playbookStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            runtimeSettings,
            capabilityResolver,
            new FakeAgentInstructionProvider(),
            new PermissiveToolApprovalPolicy(),
            NullLogger<OrchestrationResolver>.Instance);
        SeedParticipants(store, triage, specialist);

        // Active turn model is LOCAL (ToolCapableModel).
        return (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;
    }

    [Test]
    public async Task ResolveAsync_ProjectsParticipantToolsLikeP3()
    {
        // Each participant's tools are projected with the same contract as single-agent resolution: offer ∩ AllowedToolNames, approval override.
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist",
            modelProfile: ToolCapableModel,
            allowedTools: ["GetCurrentTime", "NotOffered"],
            toolApprovals: new Dictionary<string, bool>
            {
                ["GetCurrentTime"] = true
            });
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), OfferTool("Calculate"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        // "NotOffered" is dropped (not in the offer); "GetCurrentTime" survives with the approval override applied.
        AssertEx.Equal(expected: 1, specialistSpec.Tools.Count);
        AssertEx.Equal("GetCurrentTime", specialistSpec.Tools[0].Name);
        AssertEx.Equal(expected: true, specialistSpec.Tools[0].RequiresApproval);
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantAllowsNoTools_StillResolvesAskUser()
    {
        // The participant seam. Each participant's tools are its own offer ∩ AllowedToolNames, so without the union an
        // orchestrated turn would lose ask_user entirely the moment the conversation routes through an orchestrator —
        // silently, on that path only.
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: []);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), AskUserOffer());
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.Equal(expected: 1, specialistSpec.Tools.Count);
        AssertEx.Equal(AskUserTool.ToolName, specialistSpec.Tools[0].Name);
        AssertEx.True(specialistSpec.Tools[0].RequiresApproval,
            "ask_user must stay approval-gated on a participant too — the flag is what routes it to the human round-trip");

        // Every participant gets it, not just the one with an empty allowed set.
        var triageSpec = resolved.Spec.Participants.Single(participant => participant.Key == triage.Id.ToString("D"));
        AssertEx.Contains(triageSpec.Tools, tool => tool.Name == AskUserTool.ToolName);
        AssertEx.Contains(triageSpec.Tools, tool => tool.Name == "GetCurrentTime");
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantWaivesAskUserApproval_KeepsItApprovalGated()
    {
        // Tighten-only compose, unchanged by the union: a participant's per-agent `false` is a no-op against a catalog
        // default of true, exactly as on the single-agent path.
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist",
            modelProfile: ToolCapableModel,
            allowedTools: [],
            toolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [AskUserTool.ToolName] = false
            });
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"), AskUserOffer());
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.True(specialistSpec.Tools.Single(tool => tool.Name == AskUserTool.ToolName).RequiresApproval,
            "a per-agent approval override can only ADD approval, so it must not waive ask_user's catalog default");
    }

    [Test]
    public async Task ResolveAsync_AppliesNodeApprovalPolicyToParticipantTools()
    {
        // A node policy must tighten an orchestration participant's tools too, or a node-wide policy is bypassable by
        // routing through orchestration. Node policy: require approval for the Network category; the specialist allows a
        // Network tool that ships auto-execute — it must resolve as approval-requiring.
        var nodePolicy = new NodeToolApprovalPolicy(new Dictionary<ToolCategory, bool>
            {
                [ToolCategory.Network] = true
            },
            new Dictionary<string, bool>(StringComparer.Ordinal));
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["mcp__x__y"]);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolverWithPolicy(out var store,
            nodePolicy,
            OfferTool("GetCurrentTime", category: ToolCategory.ReadLocal),
            OfferTool("mcp__x__y", requiresApproval: false, ToolCategory.Network));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        var networkTool = specialistSpec.Tools.Single(tool => tool.Name == "mcp__x__y");
        AssertEx.Equal(expected: true, networkTool.RequiresApproval);
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantPlaybookEnabled_FoldsActionsIntoItsInstructions()
    {
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        playbookStore.ListEnabledByAgentAsync(specialist.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(specialist.Id, "Stay terse.", priority: 1)
                     ]));

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.Equal("Instructions for Specialist\n\n## Operating Playbook\n- Stay terse.", specialistSpec.Instructions);
        // The triage participant has no enabled playbook, so its instructions stay byte-identical.
        var triageSpec = resolved.Spec.Participants.Single(participant => participant.Key == triage.Id.ToString("D"));
        AssertEx.Equal("Instructions for Triage", triageSpec.Instructions);
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantPlaybookDisabled_KeepsInstructionsByteIdentical()
    {
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: false);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: false);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.Equal("Instructions for Specialist", specialistSpec.Instructions);
        // A disabled participant playbook must not query the store at all.
        await playbookStore.DidNotReceive().ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantAboveThresholdWithQuery_InjectsTopKIntoItsInstructions()
    {
        // Per-participant playbook retrieval: a participant whose enabled set exceeds the threshold and a non-blank
        // retrievalQuery must route through the SAME shared PlaybookRetrievalSelector the single-agent path uses, so only
        // the ranker's top-k (re-ordered by Priority then CreatedAtUtc) is folded into that participant's instructions.
        var lowPriority = EnabledAction(Guid.Empty, "Prefer small commits.", priority: 5);
        var highPriority = EnabledAction(Guid.Empty, "Run the tests first.", priority: 1);
        var ignored = EnabledAction(Guid.Empty, "Write a changelog.", priority: 9);

        var ranker = new RecordingRanker([lowPriority, highPriority]);
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = BuildResolverWithRanker(out var store, out var playbookStore, ranker, threshold: 2, topK: 2, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        playbookStore.ListEnabledByAgentAsync(specialist.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([highPriority, lowPriority, ignored]));

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel, "run the tests").ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 1, ranker.CallCount);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        // Top-k of the selector, re-ordered by Priority ascending (1 before 5); the ignored third action is absent.
        AssertEx.Equal("Instructions for Specialist\n\n## Operating Playbook\n- Run the tests first.\n- Prefer small commits.", specialistSpec.Instructions);
    }

    [Test]
    public async Task ResolveAsync_DropsEdgesReferencingDroppedParticipant()
    {
        var triage = CreateDefinition(modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition(modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var ghost = CreateDefinition(modelProfile: ToolCapableModel); // listed in an edge but never seeded → does not survive
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage.Id,
            ParticipantAgentDefinitionIds = [triage.Id, specialist.Id, ghost.Id],
            Handoffs =
            [
                new OrchestrationHandoff
                {
                    FromAgentDefinitionId = triage.Id,
                    ToAgentDefinitionId = specialist.Id
                },
                new OrchestrationHandoff
                {
                    FromAgentDefinitionId = triage.Id,
                    ToAgentDefinitionId = ghost.Id
                }
            ]
        };
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: OrchestrationTopologyJson.Serialize(topology));
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        store.GetByIdAsync(ghost.Id, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 1, resolved!.Spec.Edges.Count);
        AssertEx.Equal(specialist.Id.ToString("D"), resolved.Spec.Edges[0].ToKey);
    }

    [Test]
    public async Task ResolveAsync_WhenMaxTurnsNotPositive_FallsBackToDefault()
    {
        var triage = CreateDefinition(modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition(modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage.Id,
            ParticipantAgentDefinitionIds = [triage.Id, specialist.Id],
            MaxTurnsPerAgent = 0
        };
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: OrchestrationTopologyJson.Serialize(topology));
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        AssertEx.Equal(expected: 8, resolved!.Spec.MaxTurnsPerAgent);
    }

    [Test]
    public async Task ResolveAsync_ComposesBaseScaffoldAheadOfParticipantInstructions()
    {
        // A participant prompt must be composed with the base scaffold exactly like a direct agent send
        // (AgentDefinitionResolver.ComposePromptAsync), not returned raw. Before this a participant ran with NO scaffold.
        const string scaffold = "You are a locally-run agent. Ground every claim; use tools when they help.";
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolverWithScaffold(out var store, scaffold, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.True(specialistSpec.Instructions.StartsWith(scaffold, StringComparison.Ordinal),
            "a participant prompt must be prefixed with the base scaffold, like a direct agent send.");
        AssertEx.Contains(specialistSpec.Instructions, "Instructions for Specialist");
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantPlaybookEnabled_ComposesScaffoldAheadOfPlaybookPersona()
    {
        // The scaffold is prepended AND the per-participant playbook fold still applies — composition order is scaffold,
        // then persona-with-playbook, exactly the single-agent shape.
        const string scaffold = "SCAFFOLD-LINE";
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolverWithScaffold(out var store, out var playbookStore, scaffold, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        playbookStore.ListEnabledByAgentAsync(specialist.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([
                         EnabledAction(specialist.Id, "Stay terse.", priority: 1)
                     ]));

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.Equal("SCAFFOLD-LINE\n\nInstructions for Specialist\n\n## Operating Playbook\n- Stay terse.", specialistSpec.Instructions);
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantDisablesBaseScaffold_KeepsPersonaOnly()
    {
        // A participant with DisableBaseScaffold set skips the prepend — byte-identical to the persona-only path, exactly
        // as AgentDefinitionResolver honors the flag for a direct send.
        const string scaffold = "SCAFFOLD-LINE";
        var triage = CreateDefinition("Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition("Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], disableBaseScaffold: true);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolverWithScaffold(out var store, scaffold, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = (await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false)).Orchestration;

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        AssertEx.Equal("Instructions for Specialist", specialistSpec.Instructions);
    }

    private static OrchestrationResolver CreateResolverWithScaffold(out IAgentDefinitionStore store, string scaffold, params AllowedToolDto[] offeredTools)
    {
        return CreateResolverWithScaffold(out store, out _, scaffold, offeredTools);
    }

    // Mirrors CreateResolver but wires an instruction provider with a NON-empty base scaffold, so the scaffold-composition
    // tests observe the prepend the empty-scaffold default (used everywhere else) treats as a no-op.
    private static OrchestrationResolver CreateResolverWithScaffold(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        string scaffold,
        params AllowedToolDto[] offeredTools)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(offeredTools);
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithToolCapableModels(ToolCapableModel).Build();
        return new OrchestrationResolver(store,
            playbookStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            runtimeSettings,
            NonThinkingCapabilityResolver(),
            new FakeAgentInstructionProvider
            {
                BaseScaffold = scaffold
            },
            new PermissiveToolApprovalPolicy(),
            NullLogger<OrchestrationResolver>.Instance);
    }

    private static OrchestrationResolver CreateResolver(out IAgentDefinitionStore store, params AllowedToolDto[] offeredTools)
    {
        return CreateResolver(out store, out _, offeredTools);
    }

    private static OrchestrationResolver CreateResolver(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        params AllowedToolDto[] offeredTools)
    {
        return CreateResolver(out store, out playbookStore, capabilityResolver: null, offeredTools);
    }

    private static OrchestrationResolver CreateResolver(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        IModelCapabilityResolver? capabilityResolver,
        params AllowedToolDto[] offeredTools)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        playbookStore = Substitute.For<IPlaybookActionStore>();
        // Default: no enabled playbook actions, so each participant's composed prompt stays byte-identical.
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
            return capable
                ? offeredTools
                : [.. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal))];
        });

        var runtimeSettings = StubNodeRuntimeSettings.Create().WithToolCapableModels(ToolCapableModel).Build();
        var retrievalOptions = Options.Create(new PlaybookRetrievalOptions());
        return new OrchestrationResolver(store,
            playbookStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            retrievalOptions,
            runtimeSettings,
            capabilityResolver ?? NonThinkingCapabilityResolver(),
            new FakeAgentInstructionProvider(),
            new PermissiveToolApprovalPolicy(),
            NullLogger<OrchestrationResolver>.Instance);
    }

    // Default capability stub: every model resolves NOT thinking (the safe default). Tests that assert per-participant
    // thinking pass their own configured resolver.
    private static IModelCapabilityResolver NonThinkingCapabilityResolver()
    {
        var resolver = Substitute.For<IModelCapabilityResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false, SupportsTools: true, IsCloud: false)));
        return resolver;
    }

    // Builds a resolver with a caller-supplied ranker + explicit retrieval threshold/top-k, mirroring the single-agent
    // AgentDefinitionResolverTests.BuildResolverWithRanker so the per-participant retrieval gate can be asserted.
    private static OrchestrationResolver BuildResolverWithRanker(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        IPlaybookRetrievalRanker ranker,
        int threshold,
        int topK,
        params AllowedToolDto[] offeredTools)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
            return capable
                ? offeredTools
                : [.. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal))];
        });

        var runtimeSettings = StubNodeRuntimeSettings.Create().WithToolCapableModels(ToolCapableModel).Build();
        var retrievalOptions = Options.Create(new PlaybookRetrievalOptions
        {
            RetrievalThreshold = threshold,
            TopK = topK
        });
        return new OrchestrationResolver(store, playbookStore, offerProvider, ranker, retrievalOptions, runtimeSettings, NonThinkingCapabilityResolver(), new FakeAgentInstructionProvider(),
            new PermissiveToolApprovalPolicy(), NullLogger<OrchestrationResolver>.Instance);
    }

    private static void SeedParticipants(IAgentDefinitionStore store, params AgentDefinitionRecord[] participants)
    {
        foreach (var participant in participants)
        {
            store.GetByIdAsync(participant.Id, Arg.Any<CancellationToken>()).Returns(participant);
        }
    }

    private static AgentDefinitionRecord CreateOrchestrator(string? modelProfile,
        AgentDefinitionRecord triage,
        IReadOnlyList<AgentDefinitionRecord> participants)
    {
        var topology = new OrchestrationTopology
        {
            Version = 1,
            TriageAgentDefinitionId = triage.Id,
            ParticipantAgentDefinitionIds = [.. participants.Select(static p => p.Id)]
        };
        return CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: modelProfile, topologyJson: OrchestrationTopologyJson.Serialize(topology));
    }

    private static AgentDefinitionRecord CreateDefinition(string name = "Agent",
        AgentDefinitionKind kind = AgentDefinitionKind.Single,
        IReadOnlyList<string>? allowedTools = null,
        IReadOnlyDictionary<string, bool>? toolApprovals = null,
        int version = 1,
        string? modelProfile = ToolCapableModel,
        string? reasoningEffort = null,
        string? topologyJson = null,
        bool playbookEnabled = false,
        bool disableBaseScaffold = false)
    {
        return new AgentDefinitionRecord(Guid.NewGuid(),
            name,
            "desc-" + name,
            "Instructions for " + name,
            modelProfile,
            reasoningEffort,
            kind,
            allowedTools ?? [],
            toolApprovals ?? new Dictionary<string, bool>(),
            topologyJson,
            version,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            playbookEnabled,
            DisableBaseScaffold: disableBaseScaffold);
    }

    private static PlaybookActionRecord EnabledAction(Guid agentDefinitionId, string behavior, int priority)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            behavior,
            Scope: null,
            priority,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    // Stand-in offered tools default to a concrete ReadLocal category (never Unknown) to mirror the production invariant
    // that every real offered tool declares a category; a test needing another category passes it explicitly.
    private static AllowedToolDto OfferTool(string name, bool requiresApproval = false, ToolCategory category = ToolCategory.ReadLocal)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = requiresApproval,
            Category = category
        };
    }

    // The ask_user offer descriptor as the real LocalToolOfferProvider merges it: approval-required (structural — that
    // flag is what routes the call to the out-of-stream human round-trip) and ReadLocal.
    private static AllowedToolDto AskUserOffer()
    {
        return OfferTool(AskUserTool.ToolName, requiresApproval: true, ToolCategory.ReadLocal);
    }

    // Mirrors CreateResolver but wires a caller-supplied node approval policy, so the seam test can prove the node
    // policy is applied to orchestration participants (every other factory uses the Permissive no-op floor).
    private static OrchestrationResolver CreateResolverWithPolicy(out IAgentDefinitionStore store,
        IToolApprovalPolicy toolApprovalPolicy,
        params AllowedToolDto[] offeredTools)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
            return capable
                ? offeredTools
                : [.. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal))];
        });
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithToolCapableModels(ToolCapableModel).Build();
        return new OrchestrationResolver(store,
            playbookStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            runtimeSettings,
            NonThinkingCapabilityResolver(),
            new FakeAgentInstructionProvider(),
            toolApprovalPolicy,
            NullLogger<OrchestrationResolver>.Instance);
    }

    // A fake ranker recording how many times it was consulted and returning a fixed (deliberately out-of-order)
    // selection, so the test can assert the gate (consulted once, above threshold + non-blank query) and the
    // selector's re-order of the ranker's output.
    private sealed class RecordingRanker(IReadOnlyList<PlaybookActionRecord>? selection = null) : IPlaybookRetrievalRanker
    {
        private readonly IReadOnlyList<PlaybookActionRecord>? _selection = selection;

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
            IReadOnlyList<PlaybookActionRecord> candidates,
            int k,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_selection ?? candidates);
        }
    }
}
