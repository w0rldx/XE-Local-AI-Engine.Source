namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Controls how much detail the scheduler records per job run in <c>scheduled_job_run_events</c>. The
///     management API exposes this to the UI so operators can tune verbosity per template.
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
///     Consumed by <see cref="IScheduledJobTemplateRegistry" /> and surfaced to the management API
///     and the React template-picker UI.
/// </summary>
public sealed class ScheduledJobTemplateDescriptor
{
    /// <summary>
    ///     Stable, globally-unique identifier. Stored in <c>scheduled_job_definitions.template_id</c> and must
    ///     never change once job definitions referencing it exist in the database.
    /// </summary>
    public required string TemplateId { get; init; }

    /// <summary>Short human-readable name shown in the UI template picker.</summary>
    public required string DisplayName { get; init; }

    /// <summary>One-sentence description of what this template does.</summary>
    public required string Description { get; init; }

    /// <summary>
    ///     Optional JSON Schema (as a JSON string) that validates the <c>parameters</c> column.
    ///     <see langword="null" /> when the template accepts no parameters.
    /// </summary>
    public required string? ParameterSchema { get; init; }

    /// <summary>
    ///     Optional default parameter JSON pre-filled when a new job definition is created from this template.
    ///     <see langword="null" /> when there are no defaults.
    /// </summary>
    public required string? DefaultParameters { get; init; }

    /// <summary>
    ///     One or more <see cref="ScheduleKind" /> values that this template supports. The management API filters the
    ///     schedule-kind picker to this list.
    /// </summary>
    public required IReadOnlyList<ScheduleKind> SupportedScheduleKinds { get; init; }

    /// <summary>
    ///     The pre-selected schedule kind when creating a new job definition from this template.
    ///     Must be present in <see cref="SupportedScheduleKinds" />.
    /// </summary>
    public required ScheduleKind DefaultScheduleKind { get; init; }

    /// <summary>
    ///     Misfire policy pre-filled on new job definitions. Handlers that must not fire after a delay should
    ///     use <see cref="SchedulerMisfirePolicy.SkipMissed" />.
    /// </summary>
    public required SchedulerMisfirePolicy DefaultMisfirePolicy { get; init; }

    /// <summary>
    ///     Optional per-template cap on wall-clock runtime in seconds. <see langword="null" /> defers to the
    ///     node-level <c>SchedulerOptions.DefaultMaxRuntimeMinutes</c>.
    /// </summary>
    public required int? DefaultMaxRuntimeSeconds { get; init; }

    /// <summary>Whether operators may fire this template manually from the management UI.</summary>
    public required bool AllowManualTrigger { get; init; }

    /// <summary>
    ///     Whether the AI agent is permitted to create new job definitions from this template.
    ///     Defaults to <see langword="false" /> — handlers must opt in explicitly to agent-driven scheduling.
    /// </summary>
    public bool AllowAgentCreation { get; init; }

    /// <summary>Default verbosity level for run-history events emitted by handlers of this template.</summary>
    public HistoryDetailLevel HistoryDetailLevel { get; init; }
}
