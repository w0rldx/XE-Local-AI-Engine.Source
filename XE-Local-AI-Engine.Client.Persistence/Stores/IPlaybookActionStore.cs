namespace XE_Local_AI_Engine.Client.Persistence.Stores;

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

    /// <summary>
    ///     Compare-and-swap promotion of a <c>Suggested</c> action to <c>Enabled</c>, guarded against the promote-time
    ///     TOCTOU. In a single transaction it (1) confirms the row still exists, is still <c>Suggested</c>, and still has
    ///     <c>Version == <paramref name="expectedVersion" /></c> — so a concurrent edit (which bumps Version and clears
    ///     the eval) or a concurrent promote (which moves it off <c>Suggested</c>) after the caller validated its snapshot
    ///     makes the write fail rather than enabling on stale evidence; (2) re-checks the enabled-action count against
    ///     <paramref name="maxEnabledActions" /> adjacent to the write, so two concurrent promotes cannot both observe a
    ///     below-cap count and both enable; and only then (3) sets <c>Enabled</c>, records
    ///     <paramref name="evalResult" />, stamps <c>EnabledAtUtc</c> and bumps <c>Version</c>. The returned
    ///     <see cref="PlaybookPromotionCommit.Status" /> discriminates success from each guard failure.
    /// </summary>
    Task<PlaybookPromotionCommit> PromoteSuggestedIfCurrentAsync(Guid id,
        int expectedVersion,
        int maxEnabledActions,
        string? evalResult,
        CancellationToken cancellationToken = default);

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
    long UpdatedAtUtc,
    IReadOnlyList<Guid>? SourceFeedbackIds = null,
    double? Confidence = null,
    string? EvalResult = null,
    long? EnabledAtUtc = null,
    MemoryScope? MemoryScope = null);

/// <summary>Outcome of <see cref="IPlaybookActionStore.PromoteSuggestedIfCurrentAsync" />: the guard that blocked, or a committed promotion.</summary>
public enum PlaybookPromotionCommitStatus
{
    /// <summary>The row was current and under the cap; it is now <c>Enabled</c> and <see cref="PlaybookPromotionCommit.Record" /> carries it.</summary>
    Committed,

    /// <summary>No row has that id.</summary>
    NotFound,

    /// <summary>The row changed under the caller (Version no longer matches, or it is no longer <c>Suggested</c>) — a concurrent edit/promote.</summary>
    VersionConflict,

    /// <summary>The agent was already at <c>maxEnabledActions</c> when the write was attempted; nothing was written.</summary>
    CapReached
}

/// <summary>Result of a CAS promotion: <see cref="Record" /> is non-null only when <see cref="Status" /> is <c>Committed</c>.</summary>
public sealed record PlaybookPromotionCommit(PlaybookPromotionCommitStatus Status, PlaybookActionRecord? Record);

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
    int Priority,
    IReadOnlyList<Guid>? SourceFeedbackIds = null,
    double? Confidence = null,
    string? EvalResult = null,
    long? EnabledAtUtc = null,
    MemoryScope? MemoryScope = null);
