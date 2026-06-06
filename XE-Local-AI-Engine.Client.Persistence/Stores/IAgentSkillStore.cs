namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for the agent skill library. <c>Description</c> and <c>Body</c> are encrypted at rest by
///     the node encryption interceptors; reads return them decrypted on the <see cref="AgentSkillRecord" />. This store
///     performs no content validation — that is the application-layer service's responsibility; it owns only
///     id/version/timestamp stamping and the content-affecting version-bump rule.
/// </summary>
public interface IAgentSkillStore
{
    /// <summary>
    ///     Persists a new skill (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and <c>Version = 1</c>)
    ///     and returns the stored record with free-text columns decrypted.
    /// </summary>
    Task<AgentSkillRecord> CreateAsync(AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the skill identified by <paramref name="id" />, stamping
    ///     <c>UpdatedAtUtc</c> and incrementing <c>Version</c> only when a content-affecting field changed (Name,
    ///     Description or Body — never the <c>Enabled</c> toggle alone). Returns the updated record, or <c>null</c> when
    ///     no skill has that id.
    /// </summary>
    Task<AgentSkillRecord?> UpdateAsync(Guid id, AgentSkillInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the skill with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no skill has that id.</summary>
    Task<AgentSkillRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns every skill in the library, ordered by Name (Ordinal) for a stable list.</summary>
    Task<IReadOnlyList<AgentSkillRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resolver fast-path: the enabled skills whose <c>Id</c> is in <paramref name="ids" />, filtered to
    ///     <c>Enabled == true</c> server-side. Ids that are missing or disabled are simply absent from the result; the
    ///     resolver drops/logs them. Order is by Name (Ordinal) for a deterministic resolved set.
    /// </summary>
    Task<IReadOnlyList<AgentSkillRecord>> ListEnabledByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted agent skill. <see cref="Description" /> and <see cref="Body" /> are
///     returned in plaintext (decrypted on materialization); the store converts to and from this shape at the boundary
///     so callers never touch the encrypted byte columns.
/// </summary>
public sealed record AgentSkillRecord(
    Guid Id,
    string Name,
    string Description,
    string Body,
    bool Enabled,
    int Version,
    long CreatedAtUtc,
    long UpdatedAtUtc);

/// <summary>
///     Mutable fields of an agent skill supplied on create/update. Free text is passed as plaintext strings; the store
///     encodes <see cref="Description" /> and <see cref="Body" /> to UTF-8 bytes before the interceptors encrypt them.
/// </summary>
public sealed record AgentSkillInput(
    string Name,
    string Description,
    string Body,
    bool Enabled = true);
