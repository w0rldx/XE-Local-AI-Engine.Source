namespace XE_Local_AI_Engine.Client.Services.Agents.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

internal sealed class PlaybookActionService(IPlaybookActionStore store, IAgentDefinitionStore agentDefinitionStore) : IPlaybookActionService
{
    private readonly IPlaybookActionStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IAgentDefinitionStore _agentDefinitionStore = agentDefinitionStore ?? throw new ArgumentNullException(nameof(agentDefinitionStore));

    public async Task<PlaybookActionRecord> CreateAsync(PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);
        return await _store.AddAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        await ValidateAsync(input, cancellationToken).ConfigureAwait(false);

        // Ownership guard: the action must already belong to the agent named on the route (input.AgentDefinitionId).
        // A mismatch (or a missing action) returns null, which the endpoint maps to 404 — this blocks updating or
        // re-parenting another agent's action through this agent's nested playbook route (IDOR).
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.AgentDefinitionId != input.AgentDefinitionId)
        {
            return null;
        }

        return await _store.UpdateAsync(id, input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default)
    {
        // Same ownership guard as UpdateAsync: only delete the action when it belongs to the route agent, so one
        // agent's playbook route cannot delete another agent's action.
        var existing = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.AgentDefinitionId != agentDefinitionId)
        {
            return false;
        }

        return await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.GetByIdAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        return _store.ListByAgentAsync(agentDefinitionId, cancellationToken);
    }

    private async Task ValidateAsync(PlaybookActionInput input, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(input.Behavior))
        {
            throw new PlaybookActionValidationException("Behavior is required.");
        }

        // P1 authors only manual actions; Analysis is reserved for the deferred self-improvement phase.
        if (input.Source != PlaybookActionSource.Manual)
        {
            throw new PlaybookActionValidationException("Only Manual playbook actions can be authored in this phase.");
        }

        // The full lifecycle is persisted, but P1 accepts only the human-toggleable states; Suggested (analysis
        // proposals) and Archived are reserved for later phases.
        if (input.State is not PlaybookActionState.Enabled and not PlaybookActionState.Disabled)
        {
            throw new PlaybookActionValidationException($"State '{input.State}' is not available in this phase; use Enabled or Disabled.");
        }

        // The FK demands an existing owning agent; reject up front rather than surface a downstream constraint failure.
        var owningAgent = await _agentDefinitionStore.GetByIdAsync(input.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (owningAgent is null)
        {
            throw new PlaybookActionValidationException($"Agent definition '{input.AgentDefinitionId}' does not exist.");
        }
    }
}
