namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Controls how much detail the scheduler records per job run in <c>scheduled_job_run_events</c>. The
///     management API (Marker 3) exposes this to the UI so operators can tune verbosity per template.
/// </summary>
public enum HistoryDetailLevel
{
    /// <summary>Start, finish, and error events only.</summary>
    Summary = 0,

    /// <summary>Start, finish, error, and key milestone events emitted by the handler.</summary>
    Detailed = 1,

    /// <summary>All events including per-step progress reports (use with care — high volume).</summary>
    Verbose = 2
}

/// <summary>
///     Immutable descriptor that a <see cref="IScheduledJobHandler" /> publishes to describe its template.
///     Consumed by <see cref="IScheduledJobTemplateRegistry" /> and surfaced to the management API (Marker 3)
///     and the React template-picker UI.
/// </summary>
/// <param name="TemplateId">
///     Stable, globally-unique identifier. Stored in <c>scheduled_job_definitions.template_id</c> and must
///     never change once job definitions referencing it exist in the database.
/// </param>
/// <param name="DisplayName">Short human-readable name shown in the UI template picker.</param>
/// <param name="Description">One-sentence description of what this template does.</param>
/// <param name="ParameterSchema">
///     Optional JSON Schema (as a JSON string) that validates the <c>parameters</c> column.
///     <see langword="null" /> when the template accepts no parameters.
/// </param>
/// <param name="DefaultParameters">
///     Optional default parameter JSON pre-filled when a new job definition is created from this template.
///     <see langword="null" /> when there are no defaults.
/// </param>
/// <param name="SupportedScheduleKinds">
///     One or more <see cref="ScheduleKind" /> values that this template supports. The management API
///     (Marker 3) filters the schedule-kind picker to this list.
/// </param>
/// <param name="DefaultScheduleKind">
///     The pre-selected schedule kind when creating a new job definition from this template.
///     Must be present in <paramref name="SupportedScheduleKinds" />.
/// </param>
/// <param name="DefaultMisfirePolicy">
///     Misfire policy pre-filled on new job definitions. Handlers that must not fire after a delay should
///     use <see cref="SchedulerMisfirePolicy.SkipMissed" />.
/// </param>
/// <param name="DefaultMaxRuntimeSeconds">
///     Optional per-template cap on wall-clock runtime in seconds. <see langword="null" /> defers to the
///     node-level <c>SchedulerOptions.DefaultMaxRuntimeMinutes</c>.
/// </param>
/// <param name="AllowManualTrigger">
///     Whether operators may fire this template manually from the management UI (Marker 3).
/// </param>
/// <param name="AllowAgentCreation">
///     Whether the AI agent is permitted to create new job definitions from this template.
///     Defaults to <see langword="false" /> — handlers must opt in explicitly to agent-driven scheduling.
/// </param>
/// <param name="HistoryDetailLevel">
///     Default verbosity level for run-history events emitted by handlers of this template.
/// </param>
public sealed record ScheduledJobTemplateDescriptor(
    string TemplateId,
    string DisplayName,
    string Description,
    string? ParameterSchema,
    string? DefaultParameters,
    IReadOnlyList<ScheduleKind> SupportedScheduleKinds,
    ScheduleKind DefaultScheduleKind,
    SchedulerMisfirePolicy DefaultMisfirePolicy,
    int? DefaultMaxRuntimeSeconds,
    bool AllowManualTrigger,
    bool AllowAgentCreation = false,
    HistoryDetailLevel HistoryDetailLevel = HistoryDetailLevel.Summary);
