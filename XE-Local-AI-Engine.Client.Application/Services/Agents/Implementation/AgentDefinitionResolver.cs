namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

internal sealed class AgentDefinitionResolver : IAgentDefinitionResolver
{
    private readonly IAgentSkillStore _agentSkillStore;
    private readonly IAgentInstructionProvider _instructionProvider;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly ILogger<AgentDefinitionResolver> _logger;
    private readonly IModelCapabilityResolver _modelCapabilityResolver;
    private readonly IPlaybookActionStore _playbookActionStore;
    private readonly PlaybookRetrievalOptions _retrievalOptions;
    private readonly IPlaybookRetrievalRanker _retrievalRanker;
    private readonly IAgentDefinitionStore _store;

    public AgentDefinitionResolver(IAgentDefinitionStore store,
        IPlaybookActionStore playbookActionStore,
        IAgentSkillStore agentSkillStore,
        ILocalToolOfferProvider localToolOfferProvider,
        IPlaybookRetrievalRanker retrievalRanker,
        IOptions<PlaybookRetrievalOptions> retrievalOptions,
        IAgentInstructionProvider instructionProvider,
        IModelCapabilityResolver modelCapabilityResolver,
        ILogger<AgentDefinitionResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
        _agentSkillStore = agentSkillStore ?? throw new ArgumentNullException(nameof(agentSkillStore));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _retrievalRanker = retrievalRanker ?? throw new ArgumentNullException(nameof(retrievalRanker));
        ArgumentNullException.ThrowIfNull(retrievalOptions);
        _retrievalOptions = retrievalOptions.Value;
        _instructionProvider = instructionProvider ?? throw new ArgumentNullException(nameof(instructionProvider));
        _modelCapabilityResolver = modelCapabilityResolver ?? throw new ArgumentNullException(nameof(modelCapabilityResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true,
        bool honorModelProfile = true, bool activeModelIsCloud = false, CancellationToken cancellationToken = default)
    {
        if (agentDefinitionId is not { } definitionId)
        {
            // Unbound conversation: keep the default persona (embedded prompt, full offer, version 1).
            return null;
        }

        var definition = await _store.GetByIdAsync(definitionId, cancellationToken).ConfigureAwait(false);
        if (definition is null)
        {
            // A binding pointing at a deleted definition degrades to the default persona rather than failing the
            // turn — matches the no-FK provenance choice on the conversation column.
            _logger.LogWarning("Agent definition {AgentDefinitionId} is bound to a conversation but no longer exists; using the default persona.", definitionId);
            return null;
        }

        // The definition's pinned ModelProfile (when set) is normally the model the turn actually runs on, so gate the
        // tool offer by it — not the caller's active model — to keep capability gating and the runtime model consistent.
        // When the user explicitly picked a concrete model in the chat dropdown the caller passes honorModelProfile=false:
        // the pin is suppressed entirely so the active model wins for BOTH tool gating AND the returned ModelProfile
        // (null), letting the caller's `resolved?.ModelProfile ?? activeModel` yield the user's pick. When the definition
        // pins no profile (or the pin is suppressed) the turn keeps the caller's active model.
        var pinnedModel = honorModelProfile ? definition.ModelProfile : null;
        var effectiveModel = pinnedModel ?? activeModelId;

        // Gate the knowledge tools on the EFFECTIVE model's provider locality, not the turn's active model. When the
        // definition pins a model (including a spawned sub-agent, whose child model IS the pin) the offer keys on that
        // pinned model, so its locality must too — otherwise a cloud-pinned agent on a local-active turn would keep the
        // knowledge tools. The pin is classified through the shared capability resolver (one cache-first lookup); with no
        // pin the effective model IS the active model, so reuse the flag the caller already resolved (no extra lookup).
        var effectiveModelIsCloud = pinnedModel is null
            ? activeModelIsCloud
            : (await _modelCapabilityResolver.ResolveAsync(pinnedModel, cancellationToken).ConfigureAwait(false)).IsCloud;
        var allowedTools = ProjectAllowedTools(definition, effectiveModel, supportsTools, effectiveModelIsCloud);
        var resolvedPrompt = await ComposePromptAsync(definition, retrievalQuery, cancellationToken).ConfigureAwait(false);
        var skills = await ResolveSkillsAsync(definition, cancellationToken).ConfigureAwait(false);

        return new ResolvedAgentRuntime(resolvedPrompt,
            allowedTools,
            pinnedModel,
            definition.ReasoningEffort,
            definition.Version,
            definition.Id,
            definition.Name,
            skills,
            definition.PlaybookEnabled,
            definition.MemoryExtractionEnabled,
            effectiveModelIsCloud,
            definition.Kind);
    }

    /// <summary>
    ///     Resolves the definition's per-agent skill picklist into the enabled, decrypted skills MAF progressive
    ///     disclosure will offer. The store's fast-path filters to Enabled==true and omits missing ids; any assigned id
    ///     absent from the result (the skill was deleted or disabled) is dropped and logged by id only — never the body
    ///     or description (privacy: dropped-skill warnings carry no encrypted content). An empty/null picklist short-
    ///     circuits with no store call so the no-skills path stays byte-identical to the pre-skills resolve.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedSkill>> ResolveSkillsAsync(AgentDefinitionRecord definition, CancellationToken cancellationToken)
    {
        var assignedIds = definition.AllowedSkillIds;
        if (assignedIds is null || assignedIds.Count == 0)
        {
            return [];
        }

        var enabled = await _agentSkillStore.ListEnabledByIdsAsync(assignedIds, cancellationToken).ConfigureAwait(false);

        var resolvedIds = new HashSet<Guid>(enabled.Select(static skill => skill.Id));
        var droppedIds = assignedIds.Where(id => !resolvedIds.Contains(id)).ToArray();
        if (droppedIds.Length > 0)
        {
            _logger.LogWarning("Agent definition {AgentDefinitionId} assigns {DroppedCount} skill(s) that are missing or disabled ({DroppedSkillIds}); they were dropped.",
                definition.Id,
                droppedIds.Length,
                string.Join(", ", droppedIds));
        }

        return
        [
            .. enabled.Select(static skill => new ResolvedSkill(skill.Id, skill.Name, skill.Description, skill.Body, skill.Version))
        ];
    }

    /// <summary>
    ///     Composes the definition's final resolved prompt: the versioned base instruction scaffold (identity/
    ///     grounding/tool/output discipline), a blank line, then the persona prompt (Instructions, with playbook
    ///     memories folded in per <see cref="ComposePersonaPromptAsync" />). A definition with
    ///     <see cref="AgentDefinitionRecord.DisableBaseScaffold" /> set — or the defensive case of a blank scaffold
    ///     resource — skips the prepend entirely, keeping the resolved prompt byte-identical to the pre-scaffold
    ///     persona-only path (preserving that definition's config hash across the scaffold's introduction).
    /// </summary>
    private async Task<string> ComposePromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
    {
        var personaPrompt = await ComposePersonaPromptAsync(definition, retrievalQuery, cancellationToken).ConfigureAwait(false);
        return definition.DisableBaseScaffold
            ? personaPrompt
            : BaseInstructionComposer.Compose(_instructionProvider.GetBaseScaffold(), personaPrompt);
    }

    /// <summary>
    ///     Folds the definition's enabled playbook actions into its prompt when the playbook is enabled. When it is
    ///     disabled the query is skipped entirely and the base Instructions flow through unchanged — keeping the
    ///     resolved prompt (and thus the runtime config hash) byte-identical to the no-playbook path. When the enabled set
    ///     exceeds the retrieval threshold and a non-blank <paramref name="retrievalQuery" /> is supplied, only the
    ///     top-k most relevant actions are injected (relevance retrieval and cohort monitoring, the relevance-retrieval gate); at or below the threshold — or with a blank
    ///     query — the full static prepend is used, so the resolved prompt stays byte-identical to the pre-retrieval path.
    /// </summary>
    private async Task<string> ComposePersonaPromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
    {
        if (!definition.PlaybookEnabled)
        {
            return definition.Instructions;
        }

        var enabled = await _playbookActionStore.ListEnabledByAgentAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        var selected = await PlaybookRetrievalSelector.SelectAsync(_retrievalRanker,
            retrievalQuery,
            enabled,
            _retrievalOptions.RetrievalThreshold,
            _retrievalOptions.TopK,
            cancellationToken,
            _retrievalOptions.MaxInjectedMemoryTokens,
            _retrievalOptions.MaxInjectedFailureMemoryTokens,
            _logger).ConfigureAwait(false);
        return PlaybookPromptComposer.Compose(definition.Instructions, selected);
    }

    private IReadOnlyList<AllowedToolDto> ProjectAllowedTools(AgentDefinitionRecord definition, string? effectiveModelId, bool supportsTools, bool effectiveModelIsCloud)
    {
        // A model that does not advertise the Ollama "tools" capability cannot drive ANY tool call, so withhold the
        // entire offer (empty) before the per-tool name gating below. This is the capability gate; the offer provider's
        // ToolCapableModels name allow-list remains the additional gate for high-risk tools (run_in_agent_home / MCP).
        if (!supportsTools)
        {
            return [];
        }

        // The seeded "Default Assistant" (mode-off persona) reproduces today's chat exactly: it receives the FULL
        // capability-gated offer for the effective model, NOT the intersected allowed set (D3). It is the ONLY
        // definition granted the full offer — every other definition stays intersected (security invariant: a selected
        // agent's tool offer is never widened beyond its allowed set). The provenance is forge-proof (only the seeder
        // mints Source=Seeded with this slug), so an operator-authored row can never claim the full offer.
        if (definition.Source == AgentDefinitionSource.Seeded
            && string.Equals(definition.SeedSlug, AgentDefaults.DefaultAgentSeedSlug, StringComparison.Ordinal))
        {
            return _localToolOfferProvider.GetOfferedTools(effectiveModelId, effectiveModelIsCloud);
        }

        // Start from the PROFILE offer pool for the effective model (the whole capability-gated offer PLUS the
        // opt-in-only spawn_subagent), then keep only the tools the definition allows and override each tool's approval
        // flag per the definition. Using the profile pool — not the whole offer — is what lets a profile that lists
        // spawn_subagent resolve it while the default/mode-off path never does. Tools the definition names but the pool
        // does not contain (uninstalled or not capability-eligible) are dropped and logged — never fabricated.
        var offered = _localToolOfferProvider.GetOfferedToolsForProfile(effectiveModelId, effectiveModelIsCloud);
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
            _logger.LogWarning("Agent definition {AgentDefinitionId} names {DroppedCount} tool(s) not in the current offer ({DroppedTools}); they were dropped.",
                definition.Id,
                droppedNames.Length,
                string.Join(", ", droppedNames));
        }

        return projected;
    }
}
