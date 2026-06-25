namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Node-scoped persistence for scheduled job run events (the per-run progress/log timeline). <c>DataJson</c> is
///     encrypted at rest by the node encryption interceptors; reads return it decrypted on the
///     <see cref="ScheduledJobRunEventRecord" />. Events cascade-delete with their owning run. This store owns only
///     id/timestamp stamping.
/// </summary>
public interface IScheduledJobRunEventStore
{
    /// <summary>
    ///     Persists a new event (assigning <c>Id</c> and <c>OccurredAtUtc</c>) and returns the stored record with
    ///     <c>DataJson</c> decrypted.
    /// </summary>
    Task<ScheduledJobRunEventRecord> AddAsync(ScheduledJobRunEventInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns every event for <paramref name="runId" />, ordered by Sequence.</summary>
    Task<IReadOnlyList<ScheduledJobRunEventRecord>> ListByRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted scheduled job run event. <see cref="DataJson" /> is returned in
///     plaintext (decrypted on materialization); the store converts to and from this shape at the boundary so callers
///     never touch the encrypted byte column.
/// </summary>
public sealed record ScheduledJobRunEventRecord(
    Guid Id,
    Guid RunId,
    int Sequence,
    ScheduledRunEventLevel Level,
    string? Message,
    string? DataJson,
    long OccurredAtUtc);

/// <summary>
///     Mutable fields of a scheduled job run event supplied on create. <see cref="DataJson" /> is passed as a plaintext
///     string; the store encodes it to UTF-8 bytes before the interceptors encrypt it.
/// </summary>
public sealed record ScheduledJobRunEventInput(
    Guid RunId,
    int Sequence,
    ScheduledRunEventLevel Level,
    string? Message,
    string? DataJson = null);
