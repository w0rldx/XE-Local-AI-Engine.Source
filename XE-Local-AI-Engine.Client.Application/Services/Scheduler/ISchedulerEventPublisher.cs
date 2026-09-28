namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Publishes scheduler lifecycle notifications to connected clients.
/// </summary>
/// <remarks>
///     These messages are notifications, not the source of truth — React refetches authoritative state through TanStack
///     Query after important events. Every event is sanitized: never the raw <c>parameter_json</c>,
///     <c>details_json</c>, event <c>data_json</c>, prompts, credentials or stack traces. The default implementation is
///     a no-op (<see cref="Implementation.NullSchedulerEventPublisher" />); the Client host swaps in a hub-backed one
///     that owns the SignalR method names and the wire shapes.
/// </remarks>
public interface ISchedulerEventPublisher
{
    /// <summary>Publishes a run lifecycle transition (started / completed / failed / cancelled).</summary>
    Task PublishRunAsync(SchedulerRunEvent runEvent, CancellationToken cancellationToken = default);

    /// <summary>Publishes an intermediate progress heartbeat for an in-flight run.</summary>
    Task PublishRunProgressAsync(SchedulerRunProgressEvent progressEvent, CancellationToken cancellationToken = default);

    /// <summary>Publishes a definition change: created / updated / enabled / disabled / deleted.</summary>
    Task PublishDefinitionAsync(SchedulerDefinitionEvent definitionEvent, CancellationToken cancellationToken = default);
}

/// <summary>Which run lifecycle transition a <see cref="SchedulerRunEvent" /> reports.</summary>
public enum SchedulerRunEventKind
{
    Started,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
///     Sanitized run lifecycle event. Mirrors the safe fields of the run record — deliberately excludes
///     <c>details_json</c>, <c>error_details</c>, and parameters. <see cref="ErrorMessage" /> carries only the
///     sanitized one-line message.
/// </summary>
public sealed class SchedulerRunEvent
{
    public required SchedulerRunEventKind Kind { get; init; }

    public required Guid RunId { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string TemplateId { get; init; }

    public required ScheduledRunStatus Status { get; init; }

    public required ScheduledRunTrigger TriggeredBy { get; init; }

    public required long? ScheduledFireTimeUtc { get; init; }

    public required long? ActualFireTimeUtc { get; init; }

    public required long? CompletedAtUtc { get; init; }

    public required long? DurationMs { get; init; }

    public required string? Summary { get; init; }

    public required string? ErrorMessage { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>Sanitized progress event — a free-text message and optional percent; never structured run detail.</summary>
public sealed class SchedulerRunProgressEvent
{
    public required Guid RunId { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string? Message { get; init; }

    public required int? Percent { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>
///     Definition-change event. Carries only the definition id and a coarse lowercase action (<c>created</c>,
///     <c>updated</c>, <c>enabled</c>, <c>disabled</c>, <c>deleted</c>) — no editable field values.
/// </summary>
public sealed class SchedulerDefinitionEvent
{
    public required Guid ScheduledJobId { get; init; }

    public required string Action { get; init; }

    public required long OccurredAtUtc { get; init; }
}
