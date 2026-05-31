namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Default <see cref="IGoldenConversationService" /> (Playbook P4, D4). Validates a golden case before persisting
///     (non-blank Title, existing owning agent, non-empty InputTurns, at least one of {Assertion, Rubric}) and applies
///     the same IDOR-safe ownership guard to delete as the P1/P3 review paths. Reuses
///     <see cref="PlaybookActionValidationException" /> so callers map a validation failure the same way for both
///     playbook surfaces.
/// </summary>
internal sealed class GoldenConversationService(
    IGoldenConversationStore store,
    IAgentDefinitionStore agentDefinitionStore) : IGoldenConversationService
{
    // Boundary length caps (mirror the PlaybookAction free-text 20_000 cap). The Title is a short operator label, the
    // serialized turns can be larger (multi-turn conversations), and the assertion/rubric hold scoring text.
    private const int MaxTitleLength = 200;
    private const int MaxInputTurnsLength = 50_000;
    private const int MaxRubricLength = 20_000;
    private const int MaxAssertionLength = 20_000;

    private readonly IGoldenConversationStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));

    public async Task<GoldenConversationRecord> CreateAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new PlaybookActionValidationException("Title is required.");
        }

        if (string.IsNullOrWhiteSpace(input.InputTurns))
        {
            throw new PlaybookActionValidationException("InputTurns is required.");
        }

        // D2: a golden case must carry at least one scoring signal — an assertion (deterministic) and/or a rubric
        // (judge). A case with neither cannot be scored, so it is rejected at the boundary.
        if (string.IsNullOrWhiteSpace(input.Assertion) && string.IsNullOrWhiteSpace(input.Rubric))
        {
            throw new PlaybookActionValidationException("A golden case must carry at least one of an Assertion or a Rubric.");
        }

        // Reject over-long fields at the boundary (never trust client-supplied lengths) — bounds the encrypted payload
        // and matches the React caps so both surfaces reject the same input.
        if (input.Title.Length > MaxTitleLength)
        {
            throw new PlaybookActionValidationException($"Title must be {MaxTitleLength} characters or fewer.");
        }

        if (input.InputTurns.Length > MaxInputTurnsLength)
        {
            throw new PlaybookActionValidationException($"InputTurns must be {MaxInputTurnsLength} characters or fewer.");
        }

        if (input.Assertion is { Length: > MaxAssertionLength })
        {
            throw new PlaybookActionValidationException($"Assertion must be {MaxAssertionLength} characters or fewer.");
        }

        if (input.Rubric is { Length: > MaxRubricLength })
        {
            throw new PlaybookActionValidationException($"Rubric must be {MaxRubricLength} characters or fewer.");
        }

        // The FK demands an existing owning agent; reject up front rather than surface a downstream constraint failure.
        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }

        var storeInput = new GoldenConversationInput(
            input.AgentDefinitionId,
            input.Title,
            input.InputTurns,
            input.Assertion,
            input.Rubric,
            input.Enabled);

        return await _store.AddAsync(storeInput, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        return _store.ListByAgentAsync(agentDefinitionId, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Ownership guard: only delete the case when it belongs to the route agent, so one agent's golden route cannot
        // delete another agent's case (IDOR).
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.AgentDefinitionId != agentDefinitionId)
        {
            return false;
        }

        return await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }
}
