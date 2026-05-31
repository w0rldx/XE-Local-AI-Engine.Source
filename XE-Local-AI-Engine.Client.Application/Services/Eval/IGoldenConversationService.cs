namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Application-layer orchestration over <see cref="IGoldenConversationStore" /> for the Playbook P4 golden set
///     (manual authoring only, D4). Validates the supplied fields (non-blank Title, existing owning agent, non-empty
///     InputTurns, at least one of {Assertion, Rubric}) and delegates persistence to the store. Create/List/Delete only;
///     delete is ownership-guarded so one agent's route cannot touch another agent's golden case.
/// </summary>
public interface IGoldenConversationService
{
    /// <summary>Validates and persists a new golden case, returning the stored record.</summary>
    Task<GoldenConversationRecord> CreateAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns every golden case for <paramref name="agentDefinitionId" />, ordered by CreatedAtUtc.</summary>
    Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the golden case with <paramref name="id" /> only when it belongs to <paramref name="agentDefinitionId" />
    ///     (the agent named on the route). Returns <c>true</c> when a row was deleted, <c>false</c> when no case has that
    ///     id or it belongs to a different agent — the same ownership guard as the P1/P3 review paths.
    /// </summary>
    Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Mutable fields of a golden case supplied on create. <see cref="Enabled" /> defaults to <c>true</c> so a new case
///     participates in the next eval run unless the operator parks it.
/// </summary>
public sealed record GoldenConversationCreateInput(
    Guid AgentDefinitionId,
    string Title,
    string InputTurns,
    string? Assertion,
    string? Rubric,
    bool Enabled = true);
