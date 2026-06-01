namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Publishes scheduler lifecycle notifications to connected clients. SignalR messages are notifications, not the
///     source of truth — React refetches authoritative state through TanStack Query after important events. All payloads
///     are sanitized DTOs: never the raw <c>parameter_json</c>, <c>details_json</c>, event <c>data_json</c>, prompts,
///     credentials, or stack traces. The default implementation is a no-op (<see cref="Implementation.NullSchedulerEventPublisher" />);
///     the Client host swaps in a hub-backed publisher.
/// </summary>
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
public sealed record SchedulerRunHubEvent(
    string EventType,
    Guid RunId,
    Guid ScheduledJobId,
    string TemplateId,
    ScheduledRunStatus Status,
    ScheduledRunTrigger TriggeredBy,
    long? ScheduledFireTimeUtc,
    long? ActualFireTimeUtc,
    long? CompletedAtUtc,
    long? DurationMs,
    string? Summary,
    string? ErrorMessage,
    long OccurredAtUtc);

/// <summary>Sanitized progress payload — a free-text message and optional percent; never structured run detail.</summary>
public sealed record SchedulerRunProgressHubEvent(
    string EventType,
    Guid RunId,
    Guid ScheduledJobId,
    string? Message,
    int? Percent,
    long OccurredAtUtc);

/// <summary>Definition-change payload. Carries only the definition id and a coarse action — no editable field values.</summary>
public sealed record SchedulerDefinitionHubEvent(
    string EventType,
    Guid ScheduledJobId,
    string Action,
    long OccurredAtUtc);
