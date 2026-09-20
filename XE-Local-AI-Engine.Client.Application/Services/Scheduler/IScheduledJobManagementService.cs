namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer orchestration over the scheduled-job stores and the live Quartz scheduler.
/// </summary>
/// <remarks>
///     It validates the supplied schedule (template exists, schedule-kind supported, cron/interval/one-shot fields
///     well-formed, timezone resolves, runtime and display name present, enum values defined), persists the definition
///     through <see cref="IScheduledJobDefinitionStore" /> <em>first</em>, then reconciles the Quartz job and trigger
///     to the stored state. The store owns id/timestamp stamping and the soft-delete and enable lifecycle. Bad input
///     throws <see cref="ScheduledJobValidationException" />; a missing definition or run returns <c>null</c>.
/// </remarks>
public interface IScheduledJobManagementService
{
    /// <summary>Returns the descriptors of every registered template, in registration order.</summary>
    IReadOnlyList<ScheduledJobTemplateDescriptor> ListTemplatesAsync();

    /// <summary>
    ///     Returns every job definition ordered by creation time. Soft-deleted definitions are excluded unless
    ///     <paramref name="includeDeleted" /> is <c>true</c>.
    /// </summary>
    Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListJobsAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>Returns the definition for <paramref name="id" />, or <c>null</c> when no definition has that id.</summary>
    Task<ScheduledJobDefinitionRecord?> GetJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and persists a new definition, then schedules its Quartz job/trigger when the definition is enabled.
    ///     Returns the stored record. Throws <see cref="ScheduledJobValidationException" /> on bad input.
    /// </summary>
    Task<ScheduledJobDefinitionRecord> CreateJobAsync(ScheduledJobManagementInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies the editable fields of <paramref name="input" /> to the definition with
    ///     <paramref name="id" />, then rebuilds its Quartz job and trigger from the new definition.
    /// </summary>
    /// <remarks>
    ///     The enabling state is preserved: toggling it is the dedicated <see cref="SetEnabledAsync" /> action. Returns
    ///     the updated record, or <c>null</c> when no definition has that id.
    /// </remarks>
    Task<ScheduledJobDefinitionRecord?> UpdateJobAsync(Guid id, ScheduledJobManagementInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Enables or disables the definition with <paramref name="id" />: enabling schedules its job/trigger, disabling
    ///     unschedules it; the store stamps/clears <c>DisabledAtUtc</c>. Returns the updated record, or <c>null</c> when no
    ///     definition has that id.
    /// </summary>
    Task<ScheduledJobDefinitionRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Soft-deletes the definition with <paramref name="id" /> (preserving its run history) and unschedules its Quartz
    ///     job. Idempotent: returns <c>true</c> when a row was soft-deleted, <c>false</c> when none matched.
    /// </summary>
    Task<bool> DeleteJobAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Fires the definition with <paramref name="id" /> immediately through Quartz.
    /// </summary>
    /// <remarks>
    ///     It throws <see cref="ScheduledJobValidationException" /> when no definition has that id, when it is disabled
    ///     or deleted, when its template forbids manual triggering, or when its Quartz job is not currently scheduled.
    ///     <paramref name="parameterOverrides" /> are per-fire values stamped onto the firing trigger's
    ///     <c>JobDataMap</c>; they never mutate the stored definition, and the dispatcher decides which of them may
    ///     override a stored parameter. A <c>null</c> or empty map fires the stored parameters exactly.
    /// </remarks>
    Task TriggerNowAsync(Guid id,
        IReadOnlyDictionary<string, string>? parameterOverrides = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-adds the durable Quartz <c>JobDetail</c> with <c>replace=true</c> for every persisted, enabled,
    ///     non-deleted definition that already has a Quartz job, and returns how many were refreshed.
    /// </summary>
    /// <remarks>
    ///     A stale persisted <c>JOB_CLASS_NAME</c>, such as one written before the dispatch job moved namespaces, then
    ///     heals to the current type. It never changes a trigger's schedule and never fires a job, and skips
    ///     definitions whose template is no longer registered. Intended to run once at startup.
    /// </remarks>
    Task<int> ReconcileDurableJobsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns run-history records matching the supplied filters (each <c>null</c> filter is ignored), ordered by
    ///     actual fire time descending.
    /// </summary>
    Task<IReadOnlyList<ScheduledJobRunRecord>> ListRunsAsync(ScheduledRunStatus? status = null,
        long? fromUtc = null,
        long? toUtc = null,
        Guid? scheduledJobId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the run for <paramref name="runId" />, or <c>null</c> when no run has that id.</summary>
    Task<ScheduledJobRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Requests best-effort cancellation of the run with <paramref name="runId" />: it records
    ///     <c>CancellationRequestedAtUtc</c> and interrupts the matching Quartz fire.
    /// </summary>
    /// <remarks>
    ///     The handler's <see cref="CancellationToken" /> is signalled, and the dispatcher records the terminal
    ///     <c>Cancelled</c> state once the handler observes it. The returned <see cref="RunCancellationOutcome" /> says
    ///     whether the run was missing, already terminal, actively interrupted, or marked but not currently running.
    /// </remarks>
    Task<RunCancellationOutcome> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The editable fields of a scheduled job definition, supplied on create or update through the management API.
/// </summary>
/// <remarks>
///     <see cref="Parameters" /> is the plaintext parameter JSON, which the store encrypts at rest. Unlike the
///     persistence <c>ScheduledJobDefinitionInput</c>, this carries neither <c>Enabled</c> (create persists enabled,
///     update preserves the current state) nor <c>CreatedBy</c> (the service stamps the creator), because neither is
///     operator-editable.
/// </remarks>
public sealed class ScheduledJobManagementInput
{
    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public required string? Description { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    public required string? CronExpression { get; init; }

    public required long? IntervalSeconds { get; init; }

    public required int? RepeatCount { get; init; }

    public required long? StartAtUtc { get; init; }

    public required long? EndAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required SchedulerMisfirePolicy MisfirePolicy { get; init; }

    public required bool PreventOverlap { get; init; }

    public required int? MaxRuntimeSeconds { get; init; }

    public required string? Parameters { get; init; }
}
