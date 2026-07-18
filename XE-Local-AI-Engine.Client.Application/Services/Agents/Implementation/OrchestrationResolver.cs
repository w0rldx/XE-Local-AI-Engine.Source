namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Default <see cref="IOrchestrationResolver" />. Compiles a <c>Kind=Orchestrator</c> definition + its topology
///     into an <see cref="OrchestrationSpec" />, reusing the per-definition tool projection for every participant.
///     Every rejection path returns <c>null</c> (degrade to single-agent) and logs WHY — orchestration never fails a
///     turn, it falls back. Capability gating mirrors the single-agent path: a model is tool-capable iff it is in the
///     migrated <c>AgentHome:ToolCapableModels</c> allow-list (read via <see cref="INodeRuntimeSettings" />, the same
///     allow-list <c>LocalToolOfferProvider</c> uses).
/// </summary>
internal sealed class OrchestrationResolver : IOrchestrationResolver
{
    private const int MinimumCapableParticipants = 2;
    private const int DefaultMaxTurnsPerAgent = 8;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly ILogger<OrchestrationResolver> _logger;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly IPlaybookActionStore _playbookActionStore;
    private readonly PlaybookRetrievalOptions _retrievalOptions;
    private readonly IPlaybookRetrievalRanker _retrievalRanker;

    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IAgentDefinitionStore _store;
    private readonly IToolApprovalPolicy _toolApprovalPolicy;

    public OrchestrationResolver(IAgentDefinitionStore store,
        IPlaybookActionStore playbookActionStore,
        ILocalToolOfferProvider localToolOfferProvider,
        IPlaybookRetrievalRanker retrievalRanker,
        IOptions<PlaybookRetrievalOptions> retrievalOptions,
        INodeRuntimeSettings runtimeSettings,
        IModelCapabilityResolver modelCapabilityResolver,
        IAgentInstructionProvider instructionProvider,
        IToolApprovalPolicy toolApprovalPolicy,
        ILogger<OrchestrationResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _retrievalRanker = retrievalRanker ?? throw new ArgumentNullException(nameof(retrievalRanker));
        ArgumentNullException.ThrowIfNull(retrievalOptions);
        _retrievalOptions = retrievalOptions.Value;
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _toolApprovalPolicy = toolApprovalPolicy ?? throw new ArgumentNullException(nameof(toolApprovalPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedOrchestration?> ResolveAsync(AgentDefinitionRecord orchestrator,
        string? activeModelId,
        string? retrievalQuery = null,
        bool supportsTools = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        if (orchestrator.Kind != AgentDefinitionKind.Orchestrator)
        {
            // Single-agent definition: never an orchestration. The caller's single-agent resolver owns this case.
            return null;
        }

        // Orchestration is inherently multi-hop function calling, so a model that does not advertise the Ollama "tools"
        // capability cannot drive it: degrade the whole turn to single-agent (where the single-agent resolver withholds
        // tools as well). This is the capability gate; the ToolCapableModels name allow-list below is the additional gate.
        if (!supportsTools)
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} active model does not advertise the tools capability; degrading to single-agent.", orchestrator.Id);
            return null;
        }

        var topology = OrchestrationTopologyJson.TryParse(orchestrator.OrchestrationTopologyJson);
        if (topology is null)
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} has no usable topology; running it as a single agent.", orchestrator.Id);
            return null;
        }

        // Resolve the tool-capable allow-list once per resolve from the accessor (stored AgentHome:ToolCapableModels >
        // appsettings seed > default). OrchestrationResolver is scoped (per turn), so this one cached read picks up an
        // operator edit on the next turn without a restart.
        var toolCapableModels = await BuildToolCapableSetAsync(cancellationToken).ConfigureAwait(false);

        // The whole orchestration is gated on the orchestrator's effective model being tool-capable (handoff routing
        // is multi-hop function calling). An incapable model degrades the entire turn to single-agent — the UI warned
        // at authoring time.
        var orchestratorEffectiveModel = orchestrator.ModelProfile ?? activeModelId;
        if (!IsToolCapable(toolCapableModels, orchestratorEffectiveModel))
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} effective model is not tool-capable; degrading to single-agent.", orchestrator.Id);
            return null;
        }

        var participants = await LoadCapableParticipantsAsync(orchestrator, topology, activeModelId, retrievalQuery, toolCapableModels, cancellationToken).ConfigureAwait(false);
        if (!participants.TryGetValue(topology.TriageAgentDefinitionId, out var triage))
        {
            _logger.LogWarning("Orchestrator {AgentDefinitionId} triage participant {TriageId} is missing, deleted, or not tool-capable; degrading to single-agent.",
                orchestrator.Id,
                topology.TriageAgentDefinitionId);
            return null;
        }

        if (participants.Count < MinimumCapableParticipants)
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} resolved {ParticipantCount} capable participant(s); fewer than {Minimum} required, degrading to single-agent.",
                orchestrator.Id,
                participants.Count,
                MinimumCapableParticipants);
            return null;
        }

        var edges = BuildEdges(orchestrator, topology, participants);

        // The parser already fails an over-ceiling per-agent turn cap closed, degrading the whole turn to single agent.
        // This clamp is defense in depth for a topology ever built without going through the parser. A non-positive turn
        // cap keeps falling back to the resolver default.
        var maxTurnsPerAgent = topology.MaxTurnsPerAgent > 0
            ? Math.Min(topology.MaxTurnsPerAgent, OrchestrationTopologyJson.MaxTurnsPerAgentCeiling)
            : DefaultMaxTurnsPerAgent;

        var spec = new OrchestrationSpec
        {
            TriageParticipantKey = ToKey(triage.Definition.Id),
            Participants =
            [
                .. participants.Values
                               .Select(participant => ToSpecParticipant(participant, activeModelId))
                               .OrderBy(static participant => participant.Key, StringComparer.Ordinal)
            ],
            Edges = edges,
            MaxTurnsPerAgent = maxTurnsPerAgent,
            ReturnToPrevious = topology.ReturnToPrevious
        };

        // Aggregate participant locality: the shared seed reaches EVERY participant, so a single cloud participant makes
        // the whole turn cloud-reaching for the attachment gate. Pick the first cloud participant deterministically (by
        // definition id) so the withheld-notice names a stable model. The gate itself lives in the caller
        // (NodeChatStreamService) because attachments are staged/inlined there, not here.
        var firstCloudParticipant = participants.Values
                                                .Where(participant => participant.IsCloud)
                                                .OrderBy(participant => participant.Definition.Id)
                                                .FirstOrDefault();
        var firstCloudParticipantModel = firstCloudParticipant is null
            ? null
            : firstCloudParticipant.Definition.ModelProfile ?? activeModelId;

        return new ResolvedOrchestration(spec,
            orchestrator.Instructions,
            orchestrator.ModelProfile,
            orchestrator.ReasoningEffort,
            orchestrator.Version,
            AnyParticipantIsCloud: firstCloudParticipant is not null,
            firstCloudParticipantModel);
    }

    /// <summary>
    ///     Loads each topology participant, drops (and logs) any that no longer exist or whose pinned model is not
    ///     tool-capable, and projects each survivor's tools with the agent-definition projection logic. Returns the survivors keyed
    ///     by id, deduplicated (a participant id listed twice resolves once).
    /// </summary>
    private async Task<Dictionary<Guid, ResolvedParticipant>> LoadCapableParticipantsAsync(AgentDefinitionRecord orchestrator,
        OrchestrationTopology topology,
        string? activeModelId,
        string? retrievalQuery,
        IReadOnlySet<string> toolCapableModels,
        CancellationToken cancellationToken)
    {
        var capable = new Dictionary<Guid, ResolvedParticipant>();

        foreach (var participantId in topology.ParticipantAgentDefinitionIds.Distinct())
        {
            var participant = await _store.GetByIdAsync(participantId, cancellationToken).ConfigureAwait(false);
            if (participant is null)
            {
                _logger.LogWarning("Orchestrator {AgentDefinitionId} references participant {ParticipantId} that no longer exists; dropping it.",
                    orchestrator.Id,
                    participantId);
                continue;
            }

            // Each participant runs as its own agent, so its OWN effective model must be tool-capable; a participant
            // pinned to an incapable model is dropped/logged rather than handed a tool offer it cannot drive.
            var participantEffectiveModel = participant.ModelProfile ?? activeModelId;
            if (!IsToolCapable(toolCapableModels, participantEffectiveModel))
            {
                _logger.LogWarning("Orchestrator {AgentDefinitionId} participant {ParticipantId} effective model is not tool-capable; dropping it.",
                    orchestrator.Id,
                    participantId);
                continue;
            }

            // Resolve the participant's prompt here (in the async load) so ToSpecParticipant stays synchronous: fold in
            // its own enabled playbook when its playbook is enabled, else keep its base Instructions byte-identical.
            var resolvedInstructions = await ComposeParticipantInstructionsAsync(participant, retrievalQuery, cancellationToken).ConfigureAwait(false);

            // Resolve THIS participant's thinking capability AND provider locality from its OWN effective model (not the
            // turn model's), so a participant pinned to a non-thinking model never has a reasoning level sent to the
            // think wire, and a participant pinned to a CLOUD model has the knowledge tools gated off its offer even when
            // the turn's active model is local. Tools' tool-capability is gated separately by the ToolCapableModels
            // allow-list above; this call supplies the thinking bit and the cloud-locality bit from one lookup.
            var (supportsThinking, _, participantIsCloud) = await _modelCapabilityResolver.ResolveAsync(participantEffectiveModel, cancellationToken).ConfigureAwait(false);
            capable[participant.Id] = new ResolvedParticipant(participant, resolvedInstructions, supportsThinking, participantIsCloud);
        }

        return capable;
    }

    /// <summary>
    ///     Composes a participant's final system prompt exactly as <c>AgentDefinitionResolver.ComposePromptAsync</c>
    ///     does for a single-agent send (GPTAUD-03a): the versioned base scaffold (identity/grounding/tool/output
    ///     discipline), a blank line, then the persona prompt (its <see cref="AgentDefinitionRecord.Instructions" />
    ///     with playbook memories folded in). A participant with <see cref="AgentDefinitionRecord.DisableBaseScaffold" />
    ///     set — or the defensive blank-scaffold case — skips the prepend, keeping the prompt byte-identical to the
    ///     persona-only path. Without this a participant ran with NO base scaffold, unlike every direct agent send.
    /// </summary>
    private async Task<string> ComposeParticipantInstructionsAsync(AgentDefinitionRecord participant, string? retrievalQuery, CancellationToken cancellationToken)
    {
        var personaPrompt = await ComposeParticipantPersonaAsync(participant, retrievalQuery, cancellationToken).ConfigureAwait(false);
        return participant.DisableBaseScaffold
            ? personaPrompt
            : BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), personaPrompt);
    }

    private async Task<string> ComposeParticipantPersonaAsync(AgentDefinitionRecord participant, string? retrievalQuery, CancellationToken cancellationToken)
    {
        if (!participant.PlaybookEnabled)
        {
            return participant.Instructions;
        }

        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(participant.Id, cancellationToken).ConfigureAwait(false);
        // The SAME relevance-retrieval decision as the single-agent path (PlaybookRetrievalSelector), applied per
        // participant: below the threshold or with a blank query the full static prepend is kept byte-identical.
        var selected = await PlaybookRetrievalSelector.SelectAsync(_retrievalRanker,
            retrievalQuery,
            enabled,
            _retrievalOptions.RetrievalThreshold,
            _retrievalOptions.TopK,
            cancellationToken,
            _retrievalOptions.MaxInjectedMemoryTokens,
            _retrievalOptions.MaxInjectedFailureMemoryTokens,
            _logger).ConfigureAwait(false);
        return PlaybookPromptComposer.Compose(participant.Instructions, selected);
    }

    /// <summary>
    ///     Translates the topology's handoff edges into spec edges keyed by participant id-string, dropping (and
    ///     logging) any edge whose endpoint did not survive participant resolution. An empty result is the MAF mesh
    ///     default (every capable participant can hand off to every other).
    /// </summary>
    private IReadOnlyList<OrchestrationSpecEdge> BuildEdges(AgentDefinitionRecord orchestrator,
        OrchestrationTopology topology,
        IReadOnlyDictionary<Guid, ResolvedParticipant> participants)
    {
        var edges = new List<OrchestrationSpecEdge>();

        foreach (var handoff in topology.Handoffs)
        {
            if (!participants.ContainsKey(handoff.FromAgentDefinitionId) || !participants.ContainsKey(handoff.ToAgentDefinitionId))
            {
                _logger.LogWarning("Orchestrator {AgentDefinitionId} handoff {FromId}->{ToId} references a participant that did not survive resolution; dropping the edge.",
                    orchestrator.Id,
                    handoff.FromAgentDefinitionId,
                    handoff.ToAgentDefinitionId);
                continue;
            }

            edges.Add(new OrchestrationSpecEdge
            {
                FromKey = ToKey(handoff.FromAgentDefinitionId),
                ToKey = ToKey(handoff.ToAgentDefinitionId),
                Reason = string.IsNullOrWhiteSpace(handoff.Reason) ? null : handoff.Reason
            });
        }

        return edges;
    }

    private OrchestrationSpecParticipant ToSpecParticipant(ResolvedParticipant participant, string? activeModelId)
    {
        var definition = participant.Definition;
        return new OrchestrationSpecParticipant
        {
            Key = ToKey(definition.Id),
            Name = definition.Name,
            Description = definition.Description,
            // The playbook-composed prompt resolved during the async participant load; byte-identical to Instructions
            // when this participant's playbook is disabled.
            Instructions = participant.ResolvedInstructions,
            ModelId = definition.ModelProfile,
            ReasoningEffort = definition.ReasoningEffort,
            SupportsThinking = participant.SupportsThinking,
            // Gate on the participant's OWN effective-model locality (resolved during the async load), not the turn's
            // active model — so a cloud-pinned participant is withheld the knowledge tools even on a local-active turn.
            Tools = ProjectAllowedTools(definition, activeModelId, participant.IsCloud)
        };
    }

    /// <summary>
    ///     The same projection <c>AgentDefinitionResolver.ProjectAllowedTools</c> applies: start from the
    ///     capability-gated offer for the participant's own effective model (its pinned profile, else the turn's active
    ///     model), keep only the tools the definition allows, and override each tool's approval flag per the
    ///     definition. Names the definition allows but the offer does not contain are dropped and logged — never
    ///     fabricated, so a participant is never handed a tool the node cannot execute.
    /// </summary>
    private IReadOnlyList<AllowedToolDto> ProjectAllowedTools(AgentDefinitionRecord definition, string? activeModelId, bool effectiveModelIsCloud)
    {
        var effectiveModel = definition.ModelProfile ?? activeModelId;
        var offered = _localToolOfferProvider.GetOfferedTools(effectiveModel, effectiveModelIsCloud);
        var allowedNames = new HashSet<string>(definition.AllowedToolNames, StringComparer.Ordinal);

        var projected = offered
                        .Where(tool => allowedNames.Contains(tool.Name))
                        .Select(tool => tool with
                        {
                            // TIGHTEN-ONLY 3-tier compose (OPP-03), identical to AgentDefinitionResolver.ProjectAllowedTools
                            // so a node approval policy cannot be bypassed by routing through an orchestration participant:
                            // the node policy (which already ORs the catalog default with its category/per-tool rule) first,
                            // then the participant's per-agent override can only ADD approval.
                            RequiresApproval = _toolApprovalPolicy.RequiresApproval(tool.Name, tool.Category, tool.RequiresApproval)
                                               || (definition.ToolApprovals.TryGetValue(tool.Name, out var perAgentApproval) && perAgentApproval)
                        })
                        .ToArray();

        var droppedNames = allowedNames
                           .Where(name => !offered.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)))
                           .ToArray();
        if (droppedNames.Length > 0)
        {
            _logger.LogWarning("Orchestration participant {AgentDefinitionId} names {DroppedCount} tool(s) not in the current offer ({DroppedTools}); they were dropped.",
                definition.Id,
                droppedNames.Length,
                string.Join(", ", droppedNames));
        }

        return projected;
    }

    private async Task<IReadOnlySet<string>> BuildToolCapableSetAsync(CancellationToken cancellationToken)
    {
        var toolCapableModels = await _runtimeSettings.GetToolCapableModelsAsync(cancellationToken).ConfigureAwait(false);
        return new HashSet<string>(toolCapableModels, StringComparer.Ordinal);
    }

    private static bool IsToolCapable(IReadOnlySet<string> toolCapableModels, string? modelId)
    {
        return modelId is not null && toolCapableModels.Contains(modelId);
    }

    private static string ToKey(Guid agentDefinitionId)
    {
        return agentDefinitionId.ToString("D");
    }

    /// <summary>
    ///     A capable participant paired with the system prompt to emit for it (its base Instructions, or its
    ///     playbook-composed prompt when its own playbook is enabled), its effective model's thinking capability, and its
    ///     effective model's provider locality (<see cref="IsCloud" />). All are resolved during the async participant
    ///     load so the synchronous <see cref="ToSpecParticipant" /> stays query-free.
    /// </summary>
    private sealed record ResolvedParticipant(AgentDefinitionRecord Definition, string ResolvedInstructions, bool SupportsThinking, bool IsCloud);
}
