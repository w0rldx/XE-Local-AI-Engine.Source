namespace XE_Local_AI_Engine.Client.Persistence;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Node-scoped persistence for golden conversation cases bound to an agent definition. <c>InputTurns</c>,
///     <c>Assertion</c> and <c>Rubric</c> are encrypted at rest by the node encryption interceptors; reads return them
///     decrypted on the <see cref="GoldenConversationRecord" />. This store performs no content validation — that is the
///     application-layer service's responsibility; it owns only id/timestamp stamping.
/// </summary>
public interface IGoldenConversationStore
{
    /// <summary>
    ///     Persists a new golden case (assigning <c>Id</c>, <c>CreatedAtUtc</c> and <c>UpdatedAtUtc</c>) and returns the
    ///     stored record with free-text columns decrypted.
    /// </summary>
    Task<GoldenConversationRecord> AddAsync(GoldenConversationInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns every golden case for <paramref name="agentDefinitionId" />, ordered by CreatedAtUtc.</summary>
    Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Runner fast-path: enabled golden cases for one agent, filtered to <c>Enabled == true</c> server-side and
    ///     ordered by CreatedAtUtc.
    /// </summary>
    Task<IReadOnlyList<GoldenConversationRecord>> ListEnabledByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no golden case has that id.</summary>
    Task<GoldenConversationRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets <c>Enabled</c> on the golden case with <paramref name="id" /> and bumps <c>UpdatedAtUtc</c>. Returns the
    ///     updated record, or <c>null</c> when no golden case has that id.
    /// </summary>
    Task<GoldenConversationRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the <c>SourceMessageId</c> of every harvested golden case for <paramref name="agentDefinitionId" />
    ///     (rows whose <c>SourceMessageId</c> is set), for dedup. Projected server-side so the encrypted columns are never
    ///     decrypted.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListSourceMessageIdsByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>Removes the golden case with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted golden conversation case. <see cref="InputTurns" />,
///     <see cref="Assertion" /> and <see cref="Rubric" /> are returned in plaintext (decrypted on materialization); the
///     store converts to and from this shape at the boundary so callers never touch the encrypted byte columns.
/// </summary>
public sealed record GoldenConversationRecord(
    Guid Id,
    Guid AgentDefinitionId,
    string Title,
    string InputTurns,
    string? Assertion,
    string? Rubric,
    bool Enabled,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    GoldenConversationSource Source = GoldenConversationSource.Manual,
    Guid? SourceMessageId = null,
    Guid? SourceConversationId = null);

/// <summary>
///     Mutable fields of a golden conversation case supplied on create. Free text is passed as plaintext strings; the
///     store encodes <see cref="InputTurns" />, <see cref="Assertion" /> and <see cref="Rubric" /> to UTF-8 bytes before
///     the interceptors encrypt them.
/// </summary>
public sealed record GoldenConversationInput(
    Guid AgentDefinitionId,
    string Title,
    string InputTurns,
    string? Assertion,
    string? Rubric,
    bool Enabled,
    GoldenConversationSource Source = GoldenConversationSource.Manual,
    Guid? SourceMessageId = null,
    Guid? SourceConversationId = null);
