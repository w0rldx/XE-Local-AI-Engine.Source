namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Application-layer orchestration over the scheduled-job stores and the live Quartz scheduler. It validates the
///     supplied schedule (template exists, schedule-kind supported, cron/interval/one-shot fields well-formed, timezone
///     resolves, runtime/display-name present, enum values defined), persists the definition via
///     <see cref="IScheduledJobDefinitionStore" /> <em>first</em>, then reconciles the Quartz job/trigger to match the
///     stored state. The store owns id/timestamp stamping and the soft-delete/enable lifecycle; this service never
///     re-implements them. It returns the decrypted scheduled-job records/projections, throws
///     <see cref="ScheduledJobValidationException" /> on bad input, and returns <c>null</c> for a missing definition/run.
/// </summary>
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
    ///     <paramref name="id" />, then rebuilds its Quartz job/trigger from the new definition (enabling state is
    ///     preserved — toggling it is the dedicated <see cref="SetEnabledAsync" /> action). Returns the updated record, or
    ///     <c>null</c> when no definition has that id.
    /// </summary>
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
    ///     Fires the definition with <paramref name="id" /> immediately via Quartz. Throws
    ///     <see cref="ScheduledJobValidationException" /> when no definition has that id, when it is disabled/deleted, when
    ///     its template forbids manual triggering, or when its Quartz job is not currently scheduled.
    /// </summary>
    Task TriggerNowAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns run-history records matching the supplied filters (each <c>null</c> filter is ignored), ordered by
    ///     actual fire time descending.
    /// </summary>
    Task<IReadOnlyList<ScheduledJobRunRecord>> ListRunsAsync(
        ScheduledRunStatus? status = null,
        long? fromUtc = null,
        long? toUtc = null,
        Guid? scheduledJobId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the run for <paramref name="runId" />, or <c>null</c> when no run has that id.</summary>
    Task<ScheduledJobRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Requests best-effort cancellation of the run with <paramref name="runId" />: records
    ///     <c>CancellationRequestedAtUtc</c> and interrupts the matching Quartz fire so the handler's
    ///     <see cref="CancellationToken" /> is signalled. The dispatcher records the terminal <c>Cancelled</c> state once
    ///     the handler observes the token. Returns the <see cref="RunCancellationOutcome" /> describing whether the run was
    ///     missing, already terminal, actively interrupted, or marked but not currently running.
    /// </summary>
    Task<RunCancellationOutcome> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>
///     The editable fields of a scheduled job definition supplied on create/update through the management API.
///     <see cref="Parameters" /> is the plaintext parameter JSON; the store encrypts it at rest. Unlike the persistence
///     <c>ScheduledJobDefinitionInput</c>, this carries neither <c>Enabled</c> (create persists enabled, update preserves
///     the current state) nor <c>CreatedBy</c> (the service stamps the creator) — those are not operator-editable.
/// </summary>
public sealed record ScheduledJobManagementInput(
    string TemplateId,
    string DisplayName,
    string? Description,
    ScheduleKind ScheduleKind,
    string? CronExpression,
    long? IntervalSeconds,
    int? RepeatCount,
    long? StartAtUtc,
    long? EndAtUtc,
    string TimeZoneId,
    SchedulerMisfirePolicy MisfirePolicy,
    bool PreventOverlap,
    int? MaxRuntimeSeconds,
    string? Parameters);
