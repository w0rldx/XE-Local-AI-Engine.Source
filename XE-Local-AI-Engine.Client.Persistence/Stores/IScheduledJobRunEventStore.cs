namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Node-scoped persistence for scheduled job run events, the per-run progress and log timeline.
/// </summary>
/// <remarks>
///     <c>DataJson</c> is encrypted at rest by the node encryption interceptors; reads return it decrypted on the
///     <see cref="ScheduledJobRunEventRecord" />. Events cascade-delete with their owning run, and this store owns
///     only id and timestamp stamping.
/// </remarks>
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
public sealed class ScheduledJobRunEventRecord
{
    public required Guid Id { get; init; }

    public required Guid RunId { get; init; }

    public required int Sequence { get; init; }

    public required ScheduledRunEventLevel Level { get; init; }

    public required string? Message { get; init; }

    public required string? DataJson { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>
///     Mutable fields of a scheduled job run event supplied on create. <see cref="DataJson" /> is passed as a plaintext
///     string; the store encodes it to UTF-8 bytes before the interceptors encrypt it.
/// </summary>
public sealed class ScheduledJobRunEventInput
{
    public required Guid RunId { get; init; }

    public required int Sequence { get; init; }

    public required ScheduledRunEventLevel Level { get; init; }

    public required string? Message { get; init; }

    public string? DataJson { get; init; }
}
