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
///     Default <see cref="IOrchestrationResolver" />: compiles an orchestrator definition and its topology into an
///     <see cref="OrchestrationSpec" />, reusing the per-definition tool projection for every participant.
/// </summary>
/// <remarks>
///     Every rejection path answers a degraded <see cref="OrchestrationResolution" /> and logs WHY — orchestration
///     never fails a turn, it falls back, and the caller surfaces the reason as a notice. Capability gating mirrors
///     the single-agent path: a model is tool-capable only inside the <c>AgentHome:ToolCapableModels</c> allow-list
///     that <c>LocalToolOfferProvider</c> itself reads.
/// </remarks>
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

    public async Task<OrchestrationResolution> ResolveAsync(AgentDefinitionRecord orchestrator,
        string? activeModelId,
        string? retrievalQuery = null,
        bool supportsTools = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        if (orchestrator.Kind != AgentDefinitionKind.Orchestrator)
        {
            // Single-agent definition: never an orchestration. The caller's single-agent resolver owns this case, and
            // this is NOT a degradation — a Single-kind agent must never produce an "orchestration was not used" notice.
            return OrchestrationResolution.NotOrchestrated;
        }

        // Orchestration is multi-hop function calling, so a model that does not advertise "tools" cannot drive it and
        // the whole turn degrades. This is the capability gate; ToolCapableModels below is the additional one.
        if (!supportsTools)
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} active model does not advertise the tools capability; degrading to single-agent.", orchestrator.Id);
            return OrchestrationResolution.Degraded(OrchestrationDegradationReason.ModelNotToolCapable, "the model for this turn cannot call tools");
        }

        var topology = OrchestrationTopologyJson.TryParse(orchestrator.OrchestrationTopologyJson);
        if (topology is null)
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} has no usable topology; running it as a single agent.", orchestrator.Id);
            return OrchestrationResolution.Degraded(OrchestrationDegradationReason.TopologyInvalid, "its handoff topology is missing or invalid");
        }

        // The allow-list is read once per resolve, and this resolver is scoped per turn, so one cached read picks up
        // an operator edit on the next turn without a restart.
        var toolCapableModels = await BuildToolCapableSetAsync(cancellationToken);

        // The whole orchestration is gated on the orchestrator's effective model being tool-capable, handoff routing
        // being multi-hop function calling. An incapable model degrades the entire turn to single-agent.
        var orchestratorEffectiveModel = orchestrator.ModelProfile ?? activeModelId;
        if (!IsToolCapable(toolCapableModels, orchestratorEffectiveModel))
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} effective model is not tool-capable; degrading to single-agent.", orchestrator.Id);
            return OrchestrationResolution.Degraded(OrchestrationDegradationReason.ModelNotToolCapable, "the model for this turn cannot call tools");
        }

        var participants = await LoadCapableParticipantsAsync(orchestrator, topology, activeModelId, retrievalQuery, toolCapableModels, cancellationToken);
        if (!participants.TryGetValue(topology.TriageAgentDefinitionId, out var triage))
        {
            _logger.LogWarning("Orchestrator {AgentDefinitionId} triage participant {TriageId} is missing, deleted, or not tool-capable; degrading to single-agent.",
                orchestrator.Id,
                topology.TriageAgentDefinitionId);
            return OrchestrationResolution.Degraded(OrchestrationDegradationReason.TriageMissing, "its triage agent is missing, deleted, or cannot call tools");
        }

        if (participants.Count < MinimumCapableParticipants)
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} resolved {ParticipantCount} capable participant(s); fewer than {Minimum} required, degrading to single-agent.",
                orchestrator.Id,
                participants.Count,
                MinimumCapableParticipants);
            return OrchestrationResolution.Degraded(OrchestrationDegradationReason.TooFewCapableParticipants,
                $"only {participants.Count} of its agents can call tools, and at least {MinimumCapableParticipants} are required");
        }

        var edges = BuildEdges(orchestrator, topology, participants);

        // The parser already fails an over-ceiling turn cap closed; this clamp is defence in depth for a topology
        // built without going through it. A non-positive cap still falls back to the resolver default.
        var maxTurnsPerAgent = topology.MaxTurnsPerAgent > 0
            ? Math.Min(topology.MaxTurnsPerAgent, OrchestrationTopologyJson.MaxTurnsPerAgentCeiling)
            : DefaultMaxTurnsPerAgent;

        // Each participant's offer is projected here so its capability- and locality-gated custom tools merge in. A
        // loop rather than a Select, the projection being async; ordering by key afterwards keeps the spec stable.
        var specParticipants = new List<OrchestrationSpecParticipant>(participants.Count);
        foreach (var participant in participants.Values)
        {
            specParticipants.Add(await ToSpecParticipantAsync(participant, activeModelId, cancellationToken));
        }

        specParticipants.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));

        var spec = new OrchestrationSpec
        {
            TriageParticipantKey = ToKey(triage.Definition.Id),
            Participants = [.. specParticipants],
            Edges = edges,
            MaxTurnsPerAgent = maxTurnsPerAgent,
            ReturnToPrevious = topology.ReturnToPrevious
        };

        // The shared seed reaches EVERY participant, so one cloud participant makes the whole turn cloud-reaching for
        // the attachment gate. The first is picked by definition id, so the notice names a stable model.
        var firstCloudParticipant = participants.Values
                                                .Where(participant => participant.IsCloud)
                                                .OrderBy(participant => participant.Definition.Id)
                                                .FirstOrDefault();
        var firstCloudParticipantModel = firstCloudParticipant is null
            ? null
            : firstCloudParticipant.Definition.ModelProfile ?? activeModelId;

        return OrchestrationResolution.Compiled(new ResolvedOrchestration
        {
            Spec = spec,
            ResolvedSystemPrompt = orchestrator.Instructions,
            ModelProfile = orchestrator.ModelProfile,
            ReasoningEffort = orchestrator.ReasoningEffort,
            AgentDefinitionVersion = orchestrator.Version,
            AnyParticipantIsCloud = firstCloudParticipant is not null,
            FirstCloudParticipantModel = firstCloudParticipantModel
        });
    }

    /// <summary>
    ///     Loads each topology participant, drops and logs any that no longer exists or whose pinned model is not
    ///     tool-capable, and projects each survivor's tools with the agent-definition projection.
    /// </summary>
    /// <remarks>Survivors come back keyed by id and deduplicated: a participant listed twice resolves once.</remarks>
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
            var participant = await _store.GetByIdAsync(participantId, cancellationToken);
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
            var resolvedInstructions = await ComposeParticipantInstructionsAsync(participant, retrievalQuery, cancellationToken);

            // THIS participant's thinking capability and locality, from its OWN effective model: a non-thinking pin
            // never reaches the think wire, and a cloud pin loses the knowledge tools even on a local turn.
            var participantCapabilities = await _modelCapabilityResolver.ResolveAsync(participantEffectiveModel, cancellationToken);
            var (supportsThinking, _, participantIsCloud) = participantCapabilities;
            capable[participant.Id] = new ResolvedParticipant
            {
                Definition = participant,
                ResolvedInstructions = resolvedInstructions,
                SupportsThinking = supportsThinking,
                IsCloud = participantIsCloud,
                ReasoningBudgetEnforceable = participantCapabilities.ReasoningBudgetEnforceable
            };
        }

        return capable;
    }

    /// <summary>
    ///     Composes a participant's final system prompt exactly as <c>AgentDefinitionResolver.ComposePromptAsync</c>
    ///     does for a single-agent send: the versioned base scaffold, a blank line, then the persona prompt.
    /// </summary>
    /// <remarks>
    ///     A participant with <see cref="AgentDefinitionRecord.DisableBaseScaffold" /> set, or the defensive
    ///     blank-scaffold case, skips the prepend, keeping the prompt byte-identical to the persona-only path.
    ///     Without this a participant ran with NO base scaffold, unlike every direct agent send.
    /// </remarks>
    private async Task<string> ComposeParticipantInstructionsAsync(AgentDefinitionRecord participant, string? retrievalQuery, CancellationToken cancellationToken)
    {
        var personaPrompt = await ComposeParticipantPersonaAsync(participant, retrievalQuery, cancellationToken);
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

        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(participant.Id, cancellationToken);
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
            _logger);
        return PlaybookPromptComposer.Compose(participant.Instructions, selected);
    }

    /// <summary>
    ///     Translates the topology's handoff edges into spec edges keyed by participant id-string, dropping and
    ///     logging any edge whose endpoint did not survive participant resolution.
    /// </summary>
    /// <remarks>An empty result is the MAF mesh default: every capable participant can hand off to every other.</remarks>
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

    private async Task<OrchestrationSpecParticipant> ToSpecParticipantAsync(ResolvedParticipant participant, string? activeModelId, CancellationToken cancellationToken)
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
            // Resolved from the same per-participant lookup as SupportsThinking: a participant pinned to a model whose
            // template renders no reasoning end marker must not carry a budget llama.cpp would silently ignore.
            ReasoningBudgetEnforceable = participant.ReasoningBudgetEnforceable,
            // Gate on the participant's OWN effective-model locality (resolved during the async load), not the turn's
            // active model — so a cloud-pinned participant is withheld the knowledge tools even on a local-active turn.
            Tools = await ProjectAllowedToolsAsync(definition, activeModelId, participant.IsCloud, cancellationToken)
        };
    }

    /// <summary>
    ///     The same projection <c>AgentDefinitionResolver.ProjectAllowedTools</c> applies, over the capability-gated
    ///     offer for the participant's own effective model.
    /// </summary>
    /// <remarks>
    ///     Only the tools the definition allows survive, each with its approval flag composed from the definition. A
    ///     name the definition allows but the offer lacks is dropped and logged, never fabricated, so a participant is
    ///     never handed a tool the node cannot execute.
    /// </remarks>
    private async Task<IReadOnlyList<AllowedToolDto>> ProjectAllowedToolsAsync(AgentDefinitionRecord definition, string? activeModelId, bool effectiveModelIsCloud, CancellationToken cancellationToken)
    {
        var effectiveModel = definition.ModelProfile ?? activeModelId;
        var offered = await _localToolOfferProvider.GetOfferedToolsAsync(effectiveModel, effectiveModelIsCloud, cancellationToken);
        var allowedNames = new HashSet<string>(definition.AllowedToolNames, StringComparer.Ordinal);

        var projected = offered
                        .Where(tool => allowedNames.Contains(tool.Name))
                        .Select(tool => tool with
                        {
                            // TIGHTEN-ONLY compose, identical to AgentDefinitionResolver.ProjectAllowedTools, so no
                            // node approval policy is bypassable by routing a call through an orchestration participant.
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

        // The same post-intersection union as AgentDefinitionResolver.ProjectAllowedTools: a participant is a live
        // agent in an interactive turn, so it can always ask the operator a question whatever its allowed names say.
        return AskUserToolOffer.EnsureOffered(projected, offered, _toolApprovalPolicy);
    }

    private async Task<IReadOnlySet<string>> BuildToolCapableSetAsync(CancellationToken cancellationToken)
    {
        var toolCapableModels = await _runtimeSettings.GetToolCapableModelsAsync(cancellationToken);
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
    ///     A capable participant paired with the prompt to emit for it, its effective model's thinking capability and
    ///     that model's provider locality.
    /// </summary>
    /// <remarks>
    ///     The prompt is its base Instructions, or its playbook-composed one when its own playbook is enabled. All
    ///     three resolve during the async participant load, so the synchronous <see cref="ToSpecParticipant" /> stays
    ///     query-free.
    /// </remarks>
    private sealed record ResolvedParticipant
    {
        public required AgentDefinitionRecord Definition { get; init; }

        public required string ResolvedInstructions { get; init; }

        public required bool SupportsThinking { get; init; }

        public required bool IsCloud { get; init; }

        public required bool ReasoningBudgetEnforceable { get; init; }
    }
}
