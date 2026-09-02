namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

/// <summary>
///     Wire projection of a <c>ScheduledJobTemplateDescriptor</c>. Surfaced to the React template-picker UI via
///     <c>GET scheduler/templates</c>.
/// </summary>
public sealed class ScheduledJobTemplateResponse
{
    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    /// <summary>Optional JSON Schema string for the job parameters; null when the template accepts no parameters.</summary>
    public string? ParameterSchema { get; init; }

    /// <summary>Optional default parameter JSON pre-filled on new definitions; null when there are no defaults.</summary>
    public string? DefaultParameters { get; init; }

    /// <summary>
    ///     Schedule kinds supported by this template. Enums serialize as their string names via the globally registered
    ///     <c>JsonStringEnumConverter</c>.
    /// </summary>
    public required IReadOnlyList<ScheduleKind> SupportedScheduleKinds { get; init; }

    public required ScheduleKind DefaultScheduleKind { get; init; }

    public required SchedulerMisfirePolicy DefaultMisfirePolicy { get; init; }

    public int? DefaultMaxRuntimeSeconds { get; init; }

    public required bool AllowManualTrigger { get; init; }

    public required bool AllowAgentCreation { get; init; }

    public required string HistoryDetailLevel { get; init; }
}

/// <summary>Response envelope for <c>GET scheduler/templates</c>.</summary>
public sealed class ListScheduledJobTemplatesResponse
{
    public required IReadOnlyList<ScheduledJobTemplateResponse> Items { get; init; }
}

/// <summary>Route-only request for <c>GET scheduler/jobs</c>. Query params are bound by FastEndpoints from the query string.</summary>
public sealed class ListScheduledJobsRequest
{
    /// <summary>When true, soft-deleted definitions are included in the result.</summary>
    public bool IncludeDeleted { get; init; }
}

/// <summary>
///     Body for <c>POST scheduler/jobs</c>. Carries all editable definition fields; <see cref="Parameters" /> is passed
///     as a plaintext JSON string and stored encrypted at rest by the node encryption interceptors. The <c>Enabled</c>
///     field is omitted — create always persists enabled; toggling is the dedicated enable/disable actions.
/// </summary>
public sealed class CreateScheduledJobRequest
{
    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    public string? CronExpression { get; init; }

    public long? IntervalSeconds { get; init; }

    public int? RepeatCount { get; init; }

    public long? StartAtUtc { get; init; }

    public long? EndAtUtc { get; init; }

    public string TimeZoneId { get; init; } = "UTC";

    public SchedulerMisfirePolicy MisfirePolicy { get; init; } = SchedulerMisfirePolicy.Smart;

    public bool PreventOverlap { get; init; }

    public int? MaxRuntimeSeconds { get; init; }

    /// <summary>
    ///     Plaintext parameter JSON. Stored encrypted; never echoed back in the job response (only
    ///     <see cref="ScheduledJobResponse.HasParameters" /> is surfaced).
    /// </summary>
    public string? Parameters { get; init; }
}

/// <summary>
///     Body for <c>PUT scheduler/jobs/{scheduledJobId}</c>. Carries the same editable fields as
///     <see cref="CreateScheduledJobRequest" />, plus the route-bound id. The <c>Enabled</c> field is omitted —
///     update preserves the current enabled state; toggling is the dedicated enable/disable actions.
/// </summary>
public sealed class UpdateScheduledJobRequest
{
    public Guid ScheduledJobId { get; init; }

    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    public string? CronExpression { get; init; }

    public long? IntervalSeconds { get; init; }

    public int? RepeatCount { get; init; }

    public long? StartAtUtc { get; init; }

    public long? EndAtUtc { get; init; }

    public string TimeZoneId { get; init; } = "UTC";

    public SchedulerMisfirePolicy MisfirePolicy { get; init; } = SchedulerMisfirePolicy.Smart;

    public bool PreventOverlap { get; init; }

    public int? MaxRuntimeSeconds { get; init; }

    /// <summary>
    ///     Plaintext parameter JSON. Stored encrypted; never echoed back in the job response (only
    ///     <see cref="ScheduledJobResponse.HasParameters" /> is surfaced).
    /// </summary>
    public string? Parameters { get; init; }
}

/// <summary>Route-only request for <c>GET/DELETE scheduler/jobs/{scheduledJobId}</c>.</summary>
public sealed class ScheduledJobRouteRequest
{
    public Guid ScheduledJobId { get; init; }
}

/// <summary>Route-only request for enable/disable/trigger actions on a job (POST, no body).</summary>
public sealed class ScheduledJobActionRequest
{
    public Guid ScheduledJobId { get; init; }
}

/// <summary>
///     Wire projection of a <c>ScheduledJobDefinitionRecord</c>. Raw <c>parameter_json</c> is intentionally omitted;
///     <see cref="HasParameters" /> signals whether parameters are configured without exposing the plaintext value.
///     Enums serialize as their string names via the globally registered <c>JsonStringEnumConverter</c>.
/// </summary>
public sealed class ScheduledJobResponse
{
    public required Guid Id { get; init; }

    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    public string? CronExpression { get; init; }

    public long? IntervalSeconds { get; init; }

    public int? RepeatCount { get; init; }

    public long? StartAtUtc { get; init; }

    public long? EndAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required SchedulerMisfirePolicy MisfirePolicy { get; init; }

    public required bool PreventOverlap { get; init; }

    public int? MaxRuntimeSeconds { get; init; }

    /// <summary>True when the job definition has parameters configured; false otherwise. Raw parameter JSON is never returned.</summary>
    public required bool HasParameters { get; init; }

    public required ScheduledJobCreator CreatedBy { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public long? DisabledAtUtc { get; init; }

    public long? DeletedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET scheduler/jobs</c>.</summary>
public sealed class ListScheduledJobsResponse
{
    public required IReadOnlyList<ScheduledJobResponse> Items { get; init; }
}

/// <summary>
///     Query-string request for <c>GET scheduler/runs</c>. All filters are optional; omitted filters are ignored by the
///     service layer.
/// </summary>
public sealed class ListScheduledJobRunsRequest
{
    /// <summary>Filter by run status (string name; null = all statuses).</summary>
    public ScheduledRunStatus? Status { get; init; }

    /// <summary>Lower bound on <c>ActualFireTimeUtc</c> (unix-ms); null = no lower bound.</summary>
    public long? FromUtc { get; init; }

    /// <summary>Upper bound on <c>ActualFireTimeUtc</c> (unix-ms); null = no upper bound.</summary>
    public long? ToUtc { get; init; }

    /// <summary>Filter by the definition id that produced the run; null = all jobs.</summary>
    public Guid? ScheduledJobId { get; init; }
}

/// <summary>Route-only request for <c>GET scheduler/runs/{runId}</c>.</summary>
public sealed class ScheduledJobRunRouteRequest
{
    public Guid RunId { get; init; }
}

/// <summary>
///     Wire projection of a <c>ScheduledJobRunRecord</c>. Raw <c>details_json</c> and <c>error_details</c> are
///     intentionally omitted; <see cref="ErrorMessage" /> (human-readable summary) and <see cref="Summary" /> are
///     safe to surface. Enums serialize as their string names via the globally registered
///     <c>JsonStringEnumConverter</c>.
/// </summary>
public sealed class ScheduledJobRunResponse
{
    public required Guid Id { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string TemplateId { get; init; }

    public required ScheduledRunTrigger TriggeredBy { get; init; }

    public required ScheduledRunStatus Status { get; init; }

    public long? ScheduledFireTimeUtc { get; init; }

    public long? ActualFireTimeUtc { get; init; }

    public long? CompletedAtUtc { get; init; }

    public long? DurationMs { get; init; }

    public string? Summary { get; init; }

    /// <summary>Human-readable error message; null when the run succeeded. Raw <c>error_details</c> is never returned.</summary>
    public string? ErrorMessage { get; init; }

    public long? CancellationRequestedAtUtc { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET scheduler/runs</c>.</summary>
public sealed class ListScheduledJobRunsResponse
{
    public required IReadOnlyList<ScheduledJobRunResponse> Items { get; init; }
}

/// <summary>
///     Response for <c>POST scheduler/runs/{runId}/cancel</c>. Cancellation is best-effort, so <see cref="Outcome" />
///     reports whether the run was actively interrupted, marked but not currently running, or already terminal (the
///     not-found case is a 404 with no body). <see cref="CancellationRequestedAtUtc" /> is the unix-ms instant the
///     request was recorded, or <c>null</c> when nothing was stamped.
/// </summary>
public sealed class ScheduledJobRunCancelResponse
{
    /// <summary>String name of the <c>RunCancellationOutcome</c> (e.g. <c>Requested</c>, <c>RequestedButNotRunning</c>, <c>AlreadyTerminal</c>).</summary>
    public required string Outcome { get; init; }

    public long? CancellationRequestedAtUtc { get; init; }
}
