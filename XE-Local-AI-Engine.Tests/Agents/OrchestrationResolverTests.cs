namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

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

        var resolved = await resolver.ResolveAsync(single, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolved is null, "A single-agent definition must never resolve to an orchestration.");
        await store.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenNoTopology_ReturnsNull()
    {
        var resolver = CreateResolver(out _, OfferTool("GetCurrentTime"));
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: null);

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolved is null, "An orchestrator with no topology must degrade to single-agent (null).");
    }

    [Test]
    public async Task ResolveAsync_WhenInvalidTopology_ReturnsNull()
    {
        var resolver = CreateResolver(out _, OfferTool("GetCurrentTime"));
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: "{ not json");

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolved is null, "An invalid topology must degrade to single-agent (null).");
    }

    [Test]
    public async Task ResolveAsync_WhenEffectiveModelNotToolCapable_ReturnsNull()
    {
        var triage = CreateDefinition(kind: AgentDefinitionKind.Single, modelProfile: ToolCapableModel);
        var specialist = CreateDefinition(kind: AgentDefinitionKind.Single, modelProfile: ToolCapableModel);
        var orchestrator = CreateOrchestrator(IncapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = await resolver.ResolveAsync(orchestrator, IncapableModel).ConfigureAwait(false);

        AssertEx.True(resolved is null, "An incapable orchestrator model must degrade the whole orchestration to single-agent.");
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

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolved is null, "A topology whose triage no longer exists must degrade to single-agent.");
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

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.True(resolved is null, "Fewer than two capable participants must degrade to single-agent.");
    }

    [Test]
    public async Task ResolveAsync_WhenValid_CompilesSpecWithTriageAndParticipants()
    {
        var triage = CreateDefinition(name: "Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition(name: "Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(triage.Id.ToString("D"), resolved!.Spec.TriageParticipantKey);
        AssertEx.Equal(2, resolved.Spec.Participants.Count);
        AssertEx.Contains(resolved.Spec.Participants, participant => participant.Key == specialist.Id.ToString("D"));
        // The orchestrator's own single-agent inputs ride alongside the spec for the degrade-safe fallback.
        AssertEx.Equal(orchestrator.Instructions, resolved.ResolvedSystemPrompt);
        AssertEx.Equal(orchestrator.Version, resolved.AgentDefinitionVersion);
    }

    [Test]
    public async Task ResolveAsync_ProjectsParticipantToolsLikeP3()
    {
        // Each participant's tools are projected with the same contract as single-agent resolution: offer ∩ AllowedToolNames, approval override.
        var triage = CreateDefinition(name: "Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition(name: "Specialist",
            modelProfile: ToolCapableModel,
            allowedTools: ["GetCurrentTime", "NotOffered"],
            toolApprovals: new Dictionary<string, bool> { ["GetCurrentTime"] = true });
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime", requiresApproval: false), OfferTool("Calculate"));
        SeedParticipants(store, triage, specialist);

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        var specialistSpec = resolved!.Spec.Participants.Single(participant => participant.Key == specialist.Id.ToString("D"));
        // "NotOffered" is dropped (not in the offer); "GetCurrentTime" survives with the approval override applied.
        AssertEx.Equal(1, specialistSpec.Tools.Count);
        AssertEx.Equal("GetCurrentTime", specialistSpec.Tools[0].Name);
        AssertEx.Equal(true, specialistSpec.Tools[0].RequiresApproval);
    }

    [Test]
    public async Task ResolveAsync_WhenParticipantPlaybookEnabled_FoldsActionsIntoItsInstructions()
    {
        var triage = CreateDefinition(name: "Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition(name: "Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        playbookStore.ListEnabledByAgentAsync(specialist.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>(
                     [
                         EnabledAction(specialist.Id, "Stay terse.", priority: 1)
                     ]));

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

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
        var triage = CreateDefinition(name: "Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: false);
        var specialist = CreateDefinition(name: "Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: false);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = CreateResolver(out var store, out var playbookStore, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

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

        var ranker = new RecordingRanker(selection: [lowPriority, highPriority]);
        var triage = CreateDefinition(name: "Triage", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"]);
        var specialist = CreateDefinition(name: "Specialist", modelProfile: ToolCapableModel, allowedTools: ["GetCurrentTime"], playbookEnabled: true);
        var orchestrator = CreateOrchestrator(ToolCapableModel, triage, [triage, specialist]);
        var resolver = BuildResolverWithRanker(out var store, out var playbookStore, ranker, threshold: 2, topK: 2, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        playbookStore.ListEnabledByAgentAsync(specialist.Id, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([highPriority, lowPriority, ignored]));

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel, "run the tests").ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(1, ranker.CallCount);
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
                new OrchestrationHandoff { FromAgentDefinitionId = triage.Id, ToAgentDefinitionId = specialist.Id },
                new OrchestrationHandoff { FromAgentDefinitionId = triage.Id, ToAgentDefinitionId = ghost.Id }
            ]
        };
        var orchestrator = CreateDefinition(kind: AgentDefinitionKind.Orchestrator, modelProfile: ToolCapableModel, topologyJson: OrchestrationTopologyJson.Serialize(topology));
        var resolver = CreateResolver(out var store, OfferTool("GetCurrentTime"));
        SeedParticipants(store, triage, specialist);
        store.GetByIdAsync(ghost.Id, Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(1, resolved!.Spec.Edges.Count);
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

        var resolved = await resolver.ResolveAsync(orchestrator, ToolCapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        AssertEx.Equal(8, resolved!.Spec.MaxTurnsPerAgent);
    }

    private static OrchestrationResolver CreateResolver(out IAgentDefinitionStore store, params AllowedToolDto[] offeredTools)
    {
        return CreateResolver(out store, out _, offeredTools);
    }

    private static OrchestrationResolver CreateResolver(out IAgentDefinitionStore store,
        out IPlaybookActionStore playbookStore,
        params AllowedToolDto[] offeredTools)
    {
        store = Substitute.For<IAgentDefinitionStore>();
        playbookStore = Substitute.For<IPlaybookActionStore>();
        // Default: no enabled playbook actions, so each participant's composed prompt stays byte-identical.
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var offerProvider = Substitute.For<ILocalToolOfferProvider>();
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
            return capable
                ? offeredTools
                : [.. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal))];
        });

        var options = Options.Create(new AgentHomeOptions { ToolCapableModels = [ToolCapableModel] });
        var retrievalOptions = Options.Create(new PlaybookRetrievalOptions());
        return new OrchestrationResolver(store,
            playbookStore,
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            retrievalOptions,
            options,
            NullLogger<OrchestrationResolver>.Instance);
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
        offerProvider.GetOfferedTools(Arg.Any<string?>()).Returns(callInfo =>
        {
            var modelId = callInfo.ArgAt<string?>(0);
            var capable = modelId is not null && string.Equals(modelId, ToolCapableModel, StringComparison.Ordinal);
            return capable
                ? offeredTools
                : [.. offeredTools.Where(static tool => !string.Equals(tool.Name, CapabilityGatedToolName, StringComparison.Ordinal))];
        });

        var options = Options.Create(new AgentHomeOptions { ToolCapableModels = [ToolCapableModel] });
        var retrievalOptions = Options.Create(new PlaybookRetrievalOptions { RetrievalThreshold = threshold, TopK = topK });
        return new OrchestrationResolver(store, playbookStore, offerProvider, ranker, retrievalOptions, options, NullLogger<OrchestrationResolver>.Instance);
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
        bool playbookEnabled = false)
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
            10,
            10,
            playbookEnabled);
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

    private static AllowedToolDto OfferTool(string name, bool requiresApproval = false)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = requiresApproval
        };
    }
}
