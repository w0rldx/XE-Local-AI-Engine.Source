namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Default <see cref="IOrchestrationResolver" />. Compiles a <c>Kind=Orchestrator</c> definition + its topology
///     into an <see cref="OrchestrationSpec" />, reusing the per-definition tool projection for every participant.
///     Every rejection path returns <c>null</c> (degrade to single-agent) and logs WHY — orchestration never fails a
///     turn, it falls back. Capability gating mirrors the single-agent path: a model is tool-capable iff it is in
///     <see cref="AgentHomeOptions.ToolCapableModels" /> (the same allow-list <c>LocalToolOfferProvider</c> uses).
/// </summary>
internal sealed class OrchestrationResolver : IOrchestrationResolver
{
    private const int MinimumCapableParticipants = 2;
    private const int DefaultMaxTurnsPerAgent = 8;

    private readonly IAgentDefinitionStore _store;
    private readonly IPlaybookActionStore _playbookActionStore;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly IPlaybookRetrievalRanker _retrievalRanker;
    private readonly PlaybookRetrievalOptions _retrievalOptions;
    private readonly HashSet<string> _toolCapableModels;
    private readonly ILogger<OrchestrationResolver> _logger;

    public OrchestrationResolver(IAgentDefinitionStore store,
        IPlaybookActionStore playbookActionStore,
        ILocalToolOfferProvider localToolOfferProvider,
        IPlaybookRetrievalRanker retrievalRanker,
        IOptions<PlaybookRetrievalOptions> retrievalOptions,
        IOptions<AgentHomeOptions> agentHomeOptions,
        ILogger<OrchestrationResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _retrievalRanker = retrievalRanker ?? throw new ArgumentNullException(nameof(retrievalRanker));
        ArgumentNullException.ThrowIfNull(retrievalOptions);
        _retrievalOptions = retrievalOptions.Value;
        ArgumentNullException.ThrowIfNull(agentHomeOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _toolCapableModels = new HashSet<string>(agentHomeOptions.Value.ToolCapableModels ?? [], StringComparer.Ordinal);
    }

    /// <summary>
    ///     A capable participant paired with the system prompt to emit for it: its base Instructions, or its
    ///     playbook-composed prompt when its own playbook is enabled. The composition is resolved during the async
    ///     participant load so the synchronous <see cref="ToSpecParticipant" /> can stay query-free.
    /// </summary>
    private sealed record ResolvedParticipant(AgentDefinitionRecord Definition, string ResolvedInstructions);

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

        // The whole orchestration is gated on the orchestrator's effective model being tool-capable (handoff routing
        // is multi-hop function calling). An incapable model degrades the entire turn to single-agent — the UI warned
        // at authoring time.
        var orchestratorEffectiveModel = orchestrator.ModelProfile ?? activeModelId;
        if (!IsToolCapable(orchestratorEffectiveModel))
        {
            _logger.LogInformation("Orchestrator {AgentDefinitionId} effective model is not tool-capable; degrading to single-agent.", orchestrator.Id);
            return null;
        }

        var participants = await LoadCapableParticipantsAsync(orchestrator, topology, activeModelId, retrievalQuery, cancellationToken).ConfigureAwait(false);
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
        var maxTurnsPerAgent = topology.MaxTurnsPerAgent > 0 ? topology.MaxTurnsPerAgent : DefaultMaxTurnsPerAgent;

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

        return new ResolvedOrchestration(spec,
            orchestrator.Instructions,
            orchestrator.ModelProfile,
            orchestrator.ReasoningEffort,
            orchestrator.Version);
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
            if (!IsToolCapable(participantEffectiveModel))
            {
                _logger.LogWarning("Orchestrator {AgentDefinitionId} participant {ParticipantId} effective model is not tool-capable; dropping it.",
                    orchestrator.Id,
                    participantId);
                continue;
            }

            // Resolve the participant's prompt here (in the async load) so ToSpecParticipant stays synchronous: fold in
            // its own enabled playbook when its playbook is enabled, else keep its base Instructions byte-identical.
            var resolvedInstructions = await ComposeParticipantInstructionsAsync(participant, retrievalQuery, cancellationToken).ConfigureAwait(false);
            capable[participant.Id] = new ResolvedParticipant(participant, resolvedInstructions);
        }

        return capable;
    }

    private async Task<string> ComposeParticipantInstructionsAsync(AgentDefinitionRecord participant, string? retrievalQuery, CancellationToken cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
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
            Tools = ProjectAllowedTools(definition, activeModelId)
        };
    }

    /// <summary>
    ///     The same projection <c>AgentDefinitionResolver.ProjectAllowedTools</c> applies: start from the
    ///     capability-gated offer for the participant's own effective model (its pinned profile, else the turn's active
    ///     model), keep only the tools the definition allows, and override each tool's approval flag per the
    ///     definition. Names the definition allows but the offer does not contain are dropped and logged — never
    ///     fabricated, so a participant is never handed a tool the node cannot execute.
    /// </summary>
    private IReadOnlyList<AllowedToolDto> ProjectAllowedTools(AgentDefinitionRecord definition, string? activeModelId)
    {
        var effectiveModel = definition.ModelProfile ?? activeModelId;
        var offered = _localToolOfferProvider.GetOfferedTools(effectiveModel);
        var allowedNames = new HashSet<string>(definition.AllowedToolNames, StringComparer.Ordinal);

        var projected = offered
                        .Where(tool => allowedNames.Contains(tool.Name))
                        .Select(tool => tool with
                        {
                            RequiresApproval = definition.ToolApprovals.GetValueOrDefault(tool.Name, tool.RequiresApproval)
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

    private bool IsToolCapable(string? modelId)
    {
        return modelId is not null && _toolCapableModels.Contains(modelId);
    }

    private static string ToKey(Guid agentDefinitionId)
    {
        return agentDefinitionId.ToString("D");
    }
}
