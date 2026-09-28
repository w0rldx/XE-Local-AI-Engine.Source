namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Persistence.Entities;

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

/// <summary>Run lifecycle push payload; <see cref="EventType" /> repeats the method name it was sent under.</summary>
internal sealed class SchedulerRunHubMessage
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

/// <summary>Run progress push payload.</summary>
internal sealed class SchedulerRunProgressHubMessage
{
    public required string EventType { get; init; }

    public required Guid RunId { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string? Message { get; init; }

    public required int? Percent { get; init; }

    public required long OccurredAtUtc { get; init; }
}

/// <summary>Definition-change push payload.</summary>
internal sealed class SchedulerDefinitionHubMessage
{
    public required string EventType { get; init; }

    public required Guid ScheduledJobId { get; init; }

    public required string Action { get; init; }

    public required long OccurredAtUtc { get; init; }
}
