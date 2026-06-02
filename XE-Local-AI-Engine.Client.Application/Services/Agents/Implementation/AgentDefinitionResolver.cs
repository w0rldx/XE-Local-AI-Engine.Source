namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;

internal sealed class AgentDefinitionResolver : IAgentDefinitionResolver
{
    private readonly IAgentDefinitionStore _store;
    private readonly IPlaybookActionStore _playbookActionStore;
    private readonly ILocalToolOfferProvider _localToolOfferProvider;
    private readonly IPlaybookRetrievalRanker _retrievalRanker;
    private readonly PlaybookRetrievalOptions _retrievalOptions;
    private readonly ILogger<AgentDefinitionResolver> _logger;

    public AgentDefinitionResolver(IAgentDefinitionStore store,
        IPlaybookActionStore playbookActionStore,
        ILocalToolOfferProvider localToolOfferProvider,
        IPlaybookRetrievalRanker retrievalRanker,
        IOptions<PlaybookRetrievalOptions> retrievalOptions,
        ILogger<AgentDefinitionResolver> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
        _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
        _retrievalRanker = retrievalRanker ?? throw new ArgumentNullException(nameof(retrievalRanker));
        ArgumentNullException.ThrowIfNull(retrievalOptions);
        _retrievalOptions = retrievalOptions.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, string? retrievalQuery = null, CancellationToken cancellationToken = default)
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

        // The definition's pinned ModelProfile (when set) is the model the turn actually runs on, so gate the tool
        // offer by it — not the caller's active model — to keep capability gating and the runtime model consistent.
        // When the definition pins no profile the turn keeps the caller's active model.
        var effectiveModel = definition.ModelProfile ?? activeModelId;
        var allowedTools = ProjectAllowedTools(definition, effectiveModel);
        var resolvedPrompt = await ComposePromptAsync(definition, retrievalQuery, cancellationToken).ConfigureAwait(false);

        return new ResolvedAgentRuntime(resolvedPrompt,
            allowedTools,
            definition.ModelProfile,
            definition.ReasoningEffort,
            definition.Version);
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
            cancellationToken).ConfigureAwait(false);
        return PlaybookPromptComposer.Compose(definition.Instructions, selected);
    }

    private IReadOnlyList<AllowedToolDto> ProjectAllowedTools(AgentDefinitionRecord definition, string? effectiveModelId)
    {
        // Start from the capability-gated offer for the effective model so AgentHome gating is preserved, then keep
        // only the tools the definition allows and override each tool's approval flag per the definition. Tools the
        // definition names but the offer does not contain (uninstalled or not capability-eligible) are dropped and
        // logged — never fabricated, so the model can never be handed a tool the node cannot execute.
        var offered = _localToolOfferProvider.GetOfferedTools(effectiveModelId);
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
