namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;

internal sealed class AgentDefinitionResolver(
    IAgentDefinitionStore store,
    ILocalToolOfferProvider localToolOfferProvider,
    ILogger<AgentDefinitionResolver> logger) : IAgentDefinitionResolver
{
    private readonly IAgentDefinitionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILocalToolOfferProvider _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
    private readonly ILogger<AgentDefinitionResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<ResolvedAgentRuntime?> ResolveAsync(Guid? agentDefinitionId, string? activeModelId, CancellationToken cancellationToken = default)
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

        return new ResolvedAgentRuntime(definition.Instructions,
            allowedTools,
            definition.ModelProfile,
            definition.ReasoningEffort,
            definition.Version);
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
