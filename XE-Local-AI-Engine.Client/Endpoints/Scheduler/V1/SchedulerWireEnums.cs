namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1;

// The transport-layer parallels of the persistence enums in XE_Local_AI_Engine.Client.Persistence.Entities, isolating the scheduler wire contract from a persistence-side
// rename. CONTRACT: member names are byte-identical and the global JsonStringEnumConverter (no naming policy) serializes them by name, so "Cron" stays "Cron" on the wire.

/// <summary>
///     Wire parallel of <c>Persistence.Entities.ScheduleKind</c>. How a scheduled job's trigger fires: a recurring
///     <see cref="Cron" /> expression, a single <see cref="OneShot" /> fire, a repeating fixed <see cref="SimpleInterval" />,
///     or a <see cref="Manual" /> on-demand job.
/// </summary>
public enum ScheduleKind
{
    Cron = 0,
    OneShot = 1,
    SimpleInterval = 2,
    Manual = 3
}

/// <summary>
///     Wire parallel of <c>Persistence.Entities.SchedulerMisfirePolicy</c>. What the scheduler does when a trigger misses
///     its fire time: let the scheduler decide (<see cref="Smart" />), <see cref="SkipMissed" /> fires, or
///     <see cref="FireOnceNow" /> to catch up with a single immediate fire.
/// </summary>
public enum SchedulerMisfirePolicy
{
    Smart = 0,
    SkipMissed = 1,
    FireOnceNow = 2
}

/// <summary>
///     Wire parallel of <c>Persistence.Entities.ScheduledJobCreator</c>. Who created a scheduled job definition: a
///     <see cref="User" />, an <see cref="Agent" />, or the <see cref="System" />.
/// </summary>
public enum ScheduledJobCreator
{
    User = 0,
    Agent = 1,
    System = 2
}

/// <summary>
///     Wire parallel of <c>Persistence.Entities.ScheduledRunStatus</c>. Lifecycle state of a single scheduled job run,
///     from <see cref="Queued" /> through a terminal outcome (<see cref="Succeeded" />, <see cref="Failed" />,
///     <see cref="Cancelled" />, <see cref="TimedOut" /> or <see cref="Skipped" />).
/// </summary>
public enum ScheduledRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5,
    Skipped = 6
}

/// <summary>
///     Wire parallel of <c>Persistence.Entities.ScheduledRunTrigger</c>. What caused a scheduled job run to fire: the
///     <see cref="Schedule" /> itself, a <see cref="Manual" /> operator action, an <see cref="Agent" />, or the
///     <see cref="System" /> (e.g. startup reconciliation).
/// </summary>
public enum ScheduledRunTrigger
{
    Schedule = 0,
    Manual = 1,
    Agent = 2,
    System = 3
}
