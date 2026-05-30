namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;

internal sealed class AgentDefinitionService(
    IAgentDefinitionStore store,
    ILocalToolOfferProvider localToolOfferProvider,
    ILogger<AgentDefinitionService> logger) : IAgentDefinitionService
{
    // The reasoning-effort values the runtime config-hash normalizer accepts; anything else (case-insensitive) would
    // be silently dropped to null downstream, so reject it up front rather than persist an unusable value.
    private static readonly IReadOnlySet<string> ValidReasoningEfforts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "none",
        "medium",
        "high"
    };

    private readonly IAgentDefinitionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILocalToolOfferProvider _localToolOfferProvider = localToolOfferProvider ?? throw new ArgumentNullException(nameof(localToolOfferProvider));
    private readonly ILogger<AgentDefinitionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task<AgentDefinitionRecord> CreateAsync(AgentDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        Validate(input);
        return _store.AddAsync(input, cancellationToken);
    }

    public Task<AgentDefinitionRecord?> UpdateAsync(Guid id, AgentDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        Validate(input);
        return _store.UpdateAsync(id, input, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(id, cancellationToken);
    }

    public Task<AgentDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<AgentDefinitionRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        return _store.ListAsync(cancellationToken);
    }

    private void Validate(AgentDefinitionInput input)
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
            throw new AgentDefinitionValidationException($"ReasoningEffort '{input.ReasoningEffort}' is not one of low, none, medium, high.");
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
        var knownToolNames = new HashSet<string>(_localToolOfferProvider.GetKnownToolNames(), StringComparer.Ordinal);
        var unknownToolNames = allowedToolNames
                               .Where(name => !knownToolNames.Contains(name))
                               .ToArray();
        if (unknownToolNames.Length > 0)
        {
            _logger.LogWarning("Agent definition references {UnknownCount} tool name(s) not in the node catalog ({UnknownTools}); they will be ignored until the tool is available.",
                unknownToolNames.Length,
                string.Join(", ", unknownToolNames));
        }
    }
}
