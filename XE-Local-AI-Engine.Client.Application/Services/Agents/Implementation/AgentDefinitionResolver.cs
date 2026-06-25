namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;

internal sealed class AgentDefinitionResolver : IAgentDefinitionResolver
{
    private readonly IAgentSkillStore _agentSkillStore;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly ILogger<AgentDefinitionResolver> _logger;
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
        ILogger<AgentDefinitionResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
        _agentSkillStore = agentSkillStore ?? throw new ArgumentNullException(nameof(agentSkillStore));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _retrievalRanker = retrievalRanker ?? throw new ArgumentNullException(nameof(retrievalRanker));
        ArgumentNullException.ThrowIfNull(retrievalOptions);
        _retrievalOptions = retrievalOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, string? retrievalQuery = null, bool supportsTools = true,
        bool honorModelProfile = true, CancellationToken cancellationToken = default)
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
        var allowedTools = ProjectAllowedTools(definition, effectiveModel, supportsTools);
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
            definition.MemoryExtractionEnabled);
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
    ///     Folds the definition's enabled playbook actions into its prompt when the playbook is enabled. When it is
    ///     disabled the query is skipped entirely and the base Instructions flow through unchanged — keeping the
    ///     resolved prompt (and thus the runtime config hash) byte-identical to the no-playbook path. When the enabled set
    ///     exceeds the retrieval threshold and a non-blank <paramref name="retrievalQuery" /> is supplied, only the
    ///     top-k most relevant actions are injected (relevance retrieval and cohort monitoring, the relevance-retrieval gate); at or below the threshold — or with a blank
    ///     query — the full static prepend is used, so the resolved prompt stays byte-identical to the pre-retrieval path.
    /// </summary>
    private async Task<string> ComposePromptAsync(AgentDefinitionRecord definition, string? retrievalQuery, CancellationToken cancellationToken)
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

    private IReadOnlyList<AllowedToolDto> ProjectAllowedTools(AgentDefinitionRecord definition, string? effectiveModelId, bool supportsTools)
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
            return _localToolOfferProvider.GetOfferedTools(effectiveModelId);
        }

        // Start from the PROFILE offer pool for the effective model (the whole capability-gated offer PLUS the
        // opt-in-only spawn_subagent), then keep only the tools the definition allows and override each tool's approval
        // flag per the definition. Using the profile pool — not the whole offer — is what lets a profile that lists
        // spawn_subagent resolve it while the default/mode-off path never does. Tools the definition names but the pool
        // does not contain (uninstalled or not capability-eligible) are dropped and logged — never fabricated.
        var offered = _localToolOfferProvider.GetOfferedToolsForProfile(effectiveModelId);
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
