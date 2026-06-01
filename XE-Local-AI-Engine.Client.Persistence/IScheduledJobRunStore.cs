namespace XE_Local_AI_Engine.Client.Persistence;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Node-scoped persistence for scheduled job run history. <c>DetailsJson</c> is encrypted at rest by the node
///     encryption interceptors; reads return it decrypted on the <see cref="ScheduledJobRunRecord" />. Runs intentionally
///     have no enforced FK to their definition so history outlives the definition; their events cascade. This store owns
///     id/timestamp stamping, idempotent fire-instance upsert, lifecycle transitions, startup reconciliation, and the
///     retention sweep.
/// </summary>
public interface IScheduledJobRunStore
{
    /// <summary>
    ///     Persists a new run (assigning <c>Id</c> and <c>CreatedAtUtc</c>) and returns the stored record with
    ///     <c>DetailsJson</c> decrypted.
    /// </summary>
    Task<ScheduledJobRunRecord> AddAsync(ScheduledJobRunInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no run has that id.</summary>
    Task<ScheduledJobRunRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns every run for <paramref name="scheduledJobId" />, ordered by ActualFireTimeUtc descending (most recent
    ///     first).
    /// </summary>
    Task<IReadOnlyList<ScheduledJobRunRecord>> ListByJobAsync(Guid scheduledJobId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns runs filtered by the supplied criteria (each <c>null</c> filter is ignored), ordered by
    ///     ActualFireTimeUtc descending. The <paramref name="fromUtc" />/<paramref name="toUtc" /> bounds match against
    ///     ActualFireTimeUtc.
    /// </summary>
    Task<IReadOnlyList<ScheduledJobRunRecord>> ListAsync(
        ScheduledRunStatus? status = null,
        long? fromUtc = null,
        long? toUtc = null,
        Guid? scheduledJobId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Idempotently upserts a run keyed on <c>QuartzFireInstanceId</c>: inserts a new run when none exists for the
    ///     instance, otherwise leaves the existing run untouched and returns it. Returns the stored record.
    /// </summary>
    Task<ScheduledJobRunRecord> UpsertByFireInstanceAsync(ScheduledJobRunInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies a lifecycle transition to the run with <paramref name="id" />: sets <c>Status</c> and the supplied
    ///     non-null fields. Returns the updated record, or <c>null</c> when no run has that id.
    /// </summary>
    Task<ScheduledJobRunRecord?> UpdateLifecycleAsync(
        Guid id,
        ScheduledRunStatus status,
        long? completedAtUtc = null,
        long? durationMs = null,
        string? summary = null,
        string? detailsJson = null,
        string? errorMessage = null,
        string? errorDetails = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stamps <c>CancellationRequestedAtUtc</c> on the run with <paramref name="id" /> without changing its
    ///     <c>Status</c> (the run stays active until its handler observes the cancellation and the dispatcher records a
    ///     terminal state). Returns the updated record, or <c>null</c> when no run has that id. Idempotent: re-stamping
    ///     simply overwrites the timestamp.
    /// </summary>
    Task<ScheduledJobRunRecord?> RequestCancellationAsync(Guid id, long requestedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Startup reconciliation: moves every run still in a non-terminal state (<c>Queued</c> or <c>Running</c>) to
    ///     <paramref name="terminalStatus" />, stamping <c>CompletedAtUtc</c> and recording <paramref name="reason" /> as
    ///     the error message. Returns the number of runs reconciled.
    /// </summary>
    Task<int> MarkStaleActiveRunsAsync(ScheduledRunStatus terminalStatus, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retention sweep: deletes every run created before <paramref name="cutoffUtc" /> (matched on CreatedAtUtc) in a
    ///     transaction; the cascade FK removes their events. Returns the number of runs deleted.
    /// </summary>
    Task<int> SweepOlderThanAsync(long cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted scheduled job run. <see cref="DetailsJson" /> is returned in plaintext
///     (decrypted on materialization); the store converts to and from this shape at the boundary so callers never touch
///     the encrypted byte column.
/// </summary>
public sealed record ScheduledJobRunRecord(
    Guid Id,
    Guid ScheduledJobId,
    string TemplateId,
    string? QuartzFireInstanceId,
    ScheduledRunTrigger TriggeredBy,
    ScheduledRunStatus Status,
    long? ScheduledFireTimeUtc,
    long? ActualFireTimeUtc,
    long? CompletedAtUtc,
    long? DurationMs,
    string? Summary,
    string? DetailsJson,
    string? ErrorMessage,
    string? ErrorDetails,
    long? CancellationRequestedAtUtc,
    long CreatedAtUtc);

/// <summary>
///     Mutable fields of a scheduled job run supplied on create/upsert. <see cref="DetailsJson" /> is passed as a
///     plaintext string; the store encodes it to UTF-8 bytes before the interceptors encrypt it.
/// </summary>
public sealed record ScheduledJobRunInput(
    Guid ScheduledJobId,
    string TemplateId,
    string? QuartzFireInstanceId,
    ScheduledRunTrigger TriggeredBy,
    ScheduledRunStatus Status,
    long? ScheduledFireTimeUtc,
    long? ActualFireTimeUtc,
    string? Summary = null,
    string? DetailsJson = null,
    string? ErrorMessage = null,
    string? ErrorDetails = null);
