namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for playbook actions bound to an agent definition. <c>Behavior</c> and
///     <c>TriggerCondition</c> are encrypted at rest by the node encryption interceptors; reads return them decrypted on
///     the <see cref="PlaybookActionRecord" />. This store performs no content validation — that is the
///     application-layer service's responsibility; it owns only id/version/timestamp stamping and the config-affecting
///     version-bump rule.
/// </summary>
public interface IPlaybookActionStore
{
    /// <summary>
    ///     Persists a new action (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and <c>Version = 1</c>)
    ///     and returns the stored record with free-text columns decrypted.
    /// </summary>
    Task<PlaybookActionRecord> AddAsync(PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the action identified by <paramref name="id" />, stamping
    ///     <c>UpdatedAtUtc</c> and incrementing <c>Version</c> only when a config-affecting field changed (Behavior,
    ///     Priority or State — never Scope/TriggerCondition alone). Returns the updated record, or <c>null</c> when no
    ///     action has that id.
    /// </summary>
    Task<PlaybookActionRecord?> UpdateAsync(Guid id, PlaybookActionInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the action with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no action has that id.</summary>
    Task<PlaybookActionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every action for <paramref name="agentDefinitionId" />, ordered by Priority then CreatedAtUtc.</summary>
    Task<IReadOnlyList<PlaybookActionRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolver fast-path: enabled actions for one agent, filtered to <c>State == Enabled</c> server-side and ordered
    ///     by Priority then CreatedAtUtc (stable tiebreak). The resolver must not re-sort beyond this.
    /// </summary>
    Task<IReadOnlyList<PlaybookActionRecord>> ListEnabledByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted playbook action. <see cref="Behavior" /> and
///     <see cref="TriggerCondition" /> are returned in plaintext (decrypted on materialization); the store converts to
///     and from this shape at the boundary so callers never touch the encrypted byte columns.
/// </summary>
public sealed record PlaybookActionRecord(
    Guid Id,
    Guid AgentDefinitionId,
    PlaybookActionState State,
    PlaybookActionSource Source,
    string? TriggerCondition,
    string Behavior,
    string? Scope,
    int Priority,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     Mutable fields of a playbook action supplied on create/update. Free text is passed as plaintext strings; the
///     store encodes <see cref="Behavior" /> and <see cref="TriggerCondition" /> to UTF-8 bytes before the interceptors
///     encrypt them.
/// </summary>
public sealed record PlaybookActionInput(
    Guid AgentDefinitionId,
    PlaybookActionState State,
    PlaybookActionSource Source,
    string? TriggerCondition,
    string Behavior,
    string? Scope,
    int Priority);
