namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Lifecycle state of a single scheduled job run, from <see cref="Queued" /> through a terminal outcome
///     (<see cref="Succeeded" />, <see cref="Failed" />, <see cref="Cancelled" />, <see cref="TimedOut" /> or
///     <see cref="Skipped" />).
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
