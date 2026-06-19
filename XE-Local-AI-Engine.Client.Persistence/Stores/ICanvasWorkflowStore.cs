namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Node-scoped persistence for canvas (Open Canvas preview) workflows. <c>GraphJson</c> — the full serialized graph,
///     including agent instructions and Start text — is encrypted at rest by the node encryption interceptors; reads
///     return it decrypted on the <see cref="CanvasWorkflowRecord" />. This store performs no graph validation — that is
///     the application-layer service's responsibility; it owns only id/version/timestamp stamping and the
///     optimistic-concurrency version-bump rule.
/// </summary>
public interface ICanvasWorkflowStore
{
    /// <summary>
    ///     Persists a new workflow (assigning <c>Id</c>, <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and
    ///     <c>Version = 1</c>) and returns the stored record with <c>GraphJson</c> decrypted.
    /// </summary>
    Task<CanvasWorkflowRecord> AddAsync(CanvasWorkflowInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies <paramref name="input" /> to the workflow identified by <paramref name="id" /> using optimistic
    ///     concurrency: the update only proceeds when <paramref name="expectedVersion" /> matches the stored version, in
    ///     which case <c>Version</c> is incremented and <c>UpdatedAtUtc</c> stamped. Returns
    ///     <see cref="CanvasWorkflowUpdateOutcome.Updated" /> with the record, <see cref="CanvasWorkflowUpdateOutcome.NotFound" />
    ///     when no workflow has that id, or <see cref="CanvasWorkflowUpdateOutcome.Conflict" /> when the expected version
    ///     is stale (drives the endpoint's 409).
    /// </summary>
    Task<CanvasWorkflowUpdateResult> UpdateAsync(Guid id, int expectedVersion, CanvasWorkflowInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes the workflow with <paramref name="id" />. Returns <c>true</c> when a row was deleted.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the full record (graph decrypted) for <paramref name="id" />, or <c>null</c> when no workflow has that
    ///     id.
    /// </summary>
    Task<CanvasWorkflowRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns every registered workflow as a summary (id/name/version/timestamps; <c>GraphJson</c> is <c>null</c> —
    ///     the encrypted blob is never loaded for a list), oldest first.
    /// </summary>
    Task<IReadOnlyList<CanvasWorkflowRecord>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Mutable fields of a canvas workflow supplied on create/update. The serialized graph is passed as a plaintext JSON
///     string; the store encodes it to UTF-8 bytes before the interceptors encrypt it.
/// </summary>
public sealed record CanvasWorkflowInput(string Name, string GraphJson);

/// <summary>Discriminates the result of <see cref="ICanvasWorkflowStore.UpdateAsync" />.</summary>
public enum CanvasWorkflowUpdateOutcome
{
    Updated = 0,
    NotFound = 1,
    Conflict = 2
}

/// <summary>
///     Outcome of an optimistic-concurrency update. <see cref="Record" /> is non-null only when
///     <see cref="Outcome" /> is <see cref="CanvasWorkflowUpdateOutcome.Updated" />.
/// </summary>
public sealed record CanvasWorkflowUpdateResult(CanvasWorkflowUpdateOutcome Outcome, CanvasWorkflowRecord? Record)
{
    public static CanvasWorkflowUpdateResult Updated(CanvasWorkflowRecord record)
    {
        return new CanvasWorkflowUpdateResult(CanvasWorkflowUpdateOutcome.Updated, record);
    }

    public static CanvasWorkflowUpdateResult NotFound()
    {
        return new CanvasWorkflowUpdateResult(CanvasWorkflowUpdateOutcome.NotFound, null);
    }

    public static CanvasWorkflowUpdateResult Conflict()
    {
        return new CanvasWorkflowUpdateResult(CanvasWorkflowUpdateOutcome.Conflict, null);
    }
}
