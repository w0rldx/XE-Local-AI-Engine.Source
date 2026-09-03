namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

internal sealed class AgentDefinitionService(
    IAgentDefinitionStore store,
    ILocalToolOfferProvider localToolOfferProvider,
    ILogger<AgentDefinitionService> logger) : IAgentDefinitionService
{
    // The triage plus at least one specialist: a single-participant orchestrator has nothing to hand off to and is a
    // user error (the runtime resolver also degrades below two capable participants, but authoring catches it first).
    private const int MinimumOrchestrationParticipants = 2;

    // The reasoning-effort values the runtime config-hash normalizer accepts; anything else (case-insensitive) would
    // be silently dropped to null downstream, so reject it up front rather than persist an unusable value.
    private static readonly IReadOnlySet<string> ValidReasoningEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "none",
        "medium",
        "high",
        // Resolved per turn by the invocation runner's reasoning-effort dispatcher, never sent to a provider as-is.
        "auto"
    };

    private readonly ILocalToolOfferProvider _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
    private readonly ILogger<AgentDefinitionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IAgentDefinitionStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<AgentDefinitionRecord> CreateAsync(AgentDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        return await _store.AddAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentDefinitionRecord?> UpdateAsync(Guid id, AgentDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        return await _store.UpdateAsync(id, input, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(id, cancellationToken);
    }

    public Task<AgentDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.GetByIdAsync(id, cancellationToken);
    }

    public async Task<AgentDefinitionRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        if (Guid.TryParse(key, out var id))
        {
            return await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }

        var definitions = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return definitions.FirstOrDefault(definition => string.Equals(definition.Name, key, StringComparison.Ordinal));
    }

    public Task<IReadOnlyList<AgentDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    private async Task ValidateAsync(AgentDefinitionInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new AgentDefinitionValidationException("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(input.Instructions))
        {
            throw new AgentDefinitionValidationException("Instructions are required.");
        }

        if (!Enum.IsDefined(input.Kind))
        {
            throw new AgentDefinitionValidationException($"Kind '{input.Kind}' is not a valid agent kind.");
        }

        if (!string.IsNullOrWhiteSpace(input.ReasoningEffort) && !ValidReasoningEfforts.Contains(input.ReasoningEffort))
        {
            throw new AgentDefinitionValidationException($"ReasoningEffort '{input.ReasoningEffort}' is not one of low, none, medium, high, auto.");
        }

        var allowedToolNames = new HashSet<string>(input.AllowedToolNames, StringComparer.Ordinal);

        // Approval overrides may only reference tools the definition allows — an approval for a tool that is not in
        // the allowed set would never be applied (the resolver intersects against the allowed names), so reject it as
        // a definition error rather than persist a dead override.
        var orphanedApprovals = input.ToolApprovals.Keys
                                     .Where(name => !allowedToolNames.Contains(name))
                                     .ToArray();
        if (orphanedApprovals.Length > 0)
        {
            throw new AgentDefinitionValidationException($"Tool approval(s) reference tools not in AllowedToolNames: {string.Join(", ", orphanedApprovals)}.");
        }

        // Unknown tool names are a warning, not a failure: a name that is not currently in the catalog may belong to a
        // tool that is reinstalled later, and the resolver already drops anything not in the live offer at runtime.
        var knownToolNames = new HashSet<string>(await _localToolOfferProvider.GetKnownToolNamesAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
        var unknownToolNames = allowedToolNames
                               .Where(name => !knownToolNames.Contains(name))
                               .ToArray();
        if (unknownToolNames.Length > 0)
        {
            _logger.LogWarning("Agent definition references {UnknownCount} tool name(s) not in the node catalog ({UnknownTools}); they will be ignored until the tool is available.",
                unknownToolNames.Length,
                string.Join(", ", unknownToolNames));
        }

        await ValidateOrchestrationTopologyAsync(input, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Validates the orchestration topology at authoring time. A <c>Kind=Orchestrator</c> definition must
    ///     carry a parseable v1 topology naming at least the triage plus one specialist, with the triage and every
    ///     handoff endpoint drawn from the participant set; a participant id that no longer exists is a warning (the
    ///     runtime resolver degrades), never a hard failure (mirrors the no-FK tolerance + the unknown-tool warning). A
    ///     <c>Kind=Single</c> definition must carry no topology, so a stray payload is rejected rather than silently kept.
    /// </summary>
    private async Task ValidateOrchestrationTopologyAsync(AgentDefinitionInput input, CancellationToken cancellationToken)
    {
        if (input.Kind != AgentDefinitionKind.Orchestrator)
        {
            // A single agent has no orchestration; a stored topology would be silently ignored at runtime, so reject it
            // as a definition error rather than persist a dead payload (mirrors the orphaned-approval rule).
            if (!string.IsNullOrWhiteSpace(input.OrchestrationTopologyJson))
            {
                throw new AgentDefinitionValidationException("OrchestrationTopologyJson is only valid for an orchestrator definition; a single agent must not carry a topology.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(input.OrchestrationTopologyJson))
        {
            throw new AgentDefinitionValidationException("An orchestrator definition requires an OrchestrationTopologyJson naming its triage and participants.");
        }

        // The shared parser is tolerant (returns null on malformed/unknown-version so the resolver can degrade); at
        // authoring time we have already confirmed the payload is non-blank, so a null result means the user supplied a
        // topology that does not parse as v1 — surface that as a definition error rather than persist an unusable value.
        var topology = OrchestrationTopologyJson.TryParse(input.OrchestrationTopologyJson);
        if (topology is null)
        {
            throw new AgentDefinitionValidationException($"OrchestrationTopologyJson is malformed or not a supported version (expected version {OrchestrationTopologyJson.CurrentVersion}).");
        }

        var participantIds = new HashSet<Guid>(topology.ParticipantAgentDefinitionIds);

        if (participantIds.Count < MinimumOrchestrationParticipants)
        {
            throw new AgentDefinitionValidationException($"An orchestrator requires at least {MinimumOrchestrationParticipants} participant agent definitions (the triage plus one specialist).");
        }

        if (!participantIds.Contains(topology.TriageAgentDefinitionId))
        {
            throw new AgentDefinitionValidationException("The triage agent definition id must be one of the participant agent definition ids.");
        }

        var danglingEndpoints = topology.Handoffs
                                        .SelectMany(handoff => new[]
                                        {
                                            handoff.FromAgentDefinitionId,
                                            handoff.ToAgentDefinitionId
                                        })
                                        .Where(endpoint => !participantIds.Contains(endpoint))
                                        .Distinct()
                                        .ToArray();
        if (danglingEndpoints.Length > 0)
        {
            throw new AgentDefinitionValidationException($"Handoff edge(s) reference agent definition id(s) that are not participants: {string.Join(", ", danglingEndpoints)}.");
        }

        // A participant id that no longer resolves is a warning, not a failure: a definition may be deleted or created
        // out of order, and the runtime resolver already drops dangling participants and degrades. The author is warned
        // so the orchestration can be repaired before it silently runs short-handed.
        var knownIds = (await _store.ListAsync(cancellationToken).ConfigureAwait(false))
                       .Select(record => record.Id)
                       .ToHashSet();
        var missingParticipants = participantIds
                                  .Where(id => !knownIds.Contains(id))
                                  .ToArray();
        if (missingParticipants.Length > 0)
        {
            _logger.LogWarning(
                "Orchestrator definition references {MissingCount} participant agent definition id(s) that do not currently exist ({MissingIds}); they will be skipped until the definition is created.",
                missingParticipants.Length,
                string.Join(", ", missingParticipants));
        }
    }
}
