namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The next automatic fire of one enabled scheduled job, as Quartz will run it. The name and kind come from the
///     persisted definition, the time from the job's live triggers.
/// </summary>
public sealed class UpcomingScheduledJob
{
    public required Guid ScheduledJobId { get; init; }

    public required string DisplayName { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    /// <summary>The earliest future fire time across the job's triggers, in UTC Unix milliseconds.</summary>
    public required long NextFireTimeUtc { get; init; }
}
