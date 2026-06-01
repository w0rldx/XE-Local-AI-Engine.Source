namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Node-local Quartz scheduler options. Bound from the <c>Scheduler</c> configuration section. Controls whether
///     the scheduler is active, concurrency, history retention, and the Quartz table prefix used when the migration
///     embeds the raw QRTZ DDL (Marker 1 — no Quartz NuGet package until Marker 2).
/// </summary>
public sealed class SchedulerOptions
{
    public const string Section = "Scheduler";

    /// <summary>
    ///     Whether the scheduler is active. When <c>false</c> the Quartz hosted service is not started and no jobs
    ///     fire, but the persistence tables and DI registrations remain in place. Defaults to <c>true</c>.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     Maximum number of jobs that may execute concurrently on this node. Must be greater than zero.
    ///     Defaults to <c>4</c>.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    ///     Number of days to retain completed <c>scheduled_job_runs</c> rows (and their cascaded events) before
    ///     the retention sweep deletes them. Must be greater than zero. Defaults to <c>30</c>.
    /// </summary>
    public int HistoryRetentionDays { get; init; } = 30;

    /// <summary>
    ///     How often (in minutes) the retention sweep runs. Must be greater than zero. Defaults to <c>60</c>.
    /// </summary>
    public int RetentionSweepIntervalMinutes { get; init; } = 60;

    /// <summary>
    ///     IANA time-zone identifier used as the default when a job definition does not specify one. Must not be
    ///     null or whitespace. Defaults to <c>"UTC"</c>.
    /// </summary>
    public string DefaultTimeZoneId { get; init; } = "UTC";

    /// <summary>
    ///     Default maximum wall-clock runtime (in minutes) for a job run before it is considered timed out. Must
    ///     be greater than zero. Defaults to <c>5</c>.
    /// </summary>
    public int DefaultMaxRuntimeMinutes { get; init; } = 5;

    /// <summary>
    ///     Table-name prefix used by the Quartz schema DDL embedded in the migration. Must not be null or
    ///     whitespace. Defaults to <c>"QRTZ_"</c>.
    /// </summary>
    public string QuartzTablePrefix { get; init; } = "QRTZ_";
}
