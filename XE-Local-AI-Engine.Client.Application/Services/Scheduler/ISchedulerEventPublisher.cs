namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Publishes scheduler lifecycle notifications to connected clients.
/// </summary>
/// <remarks>
///     These messages are notifications, not the source of truth — React refetches authoritative state through TanStack
///     Query after important events. Every payload is a sanitized DTO: never the raw <c>parameter_json</c>,
///     <c>details_json</c>, event <c>data_json</c>, prompts, credentials or stack traces. The default implementation is
///     a no-op (<see cref="Implementation.NullSchedulerEventPublisher" />); the Client host swaps in a hub-backed one.
/// </remarks>
public interface ISchedulerEventPublisher
{
    /// <summary>Publishes a run lifecycle transition (<c>runStarted</c>/<c>runCompleted</c>/<c>runFailed</c>/<c>runCancelled</c>).</summary>
    Task PublishRunAsync(SchedulerRunHubEvent runEvent, CancellationToken cancellationToken = default);

    /// <summary>Publishes an intermediate progress heartbeat for an in-flight run (<c>runProgress</c>).</summary>
    Task PublishRunProgressAsync(SchedulerRunProgressHubEvent progressEvent, CancellationToken cancellationToken = default);

    /// <summary>Publishes a definition change (<c>jobDefinitionChanged</c>): created / updated / enabled / disabled / deleted.</summary>
    Task PublishDefinitionAsync(SchedulerDefinitionHubEvent definitionEvent, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method names for scheduler events. These double as the wire event-type discriminator on the
///     payloads so the React client can subscribe per event name.
/// </summary>
public static class SchedulerHubEvents
{
    public const string JobDefinitionChanged = "scheduler.jobDefinitionChanged";
    public const string RunStarted = "scheduler.runStarted";
    public const string RunProgress = "scheduler.runProgress";
    public const string RunCompleted = "scheduler.runCompleted";
    public const string RunFailed = "scheduler.runFailed";
    public const string RunCancelled = "scheduler.runCancelled";
}

/// <summary>
///     Sanitized run lifecycle payload. Mirrors the safe fields of the run record — deliberately excludes
///     <c>details_json</c>, <c>error_details</c>, and parameters. <see cref="ErrorMessage" /> carries only the
///     sanitized one-line message.
/// </summary>
public sealed class SchedulerRunHubEvent
{
    public required string EventType { get; init; }

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

/// <summary>Sanitized progress payload — a free-text message and optional percent; never structured run detail.</summary>
public sealed class SchedulerRunProgressHubEvent
{
    public required string EventType { get; init; }

    public required Guid RunId { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string? Message { get; init; }

    public required int? Percent { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>Definition-change payload. Carries only the definition id and a coarse action — no editable field values.</summary>
public sealed class SchedulerDefinitionHubEvent
{
    public required string EventType { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string Action { get; init; }

    public required long OccurredAtUtc { get; init; }
}
