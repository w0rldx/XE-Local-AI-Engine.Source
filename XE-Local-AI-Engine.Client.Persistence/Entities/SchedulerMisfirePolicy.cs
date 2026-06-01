namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     What the scheduler does when a trigger misses its fire time: let the scheduler decide (<see cref="Smart" />),
///     <see cref="SkipMissed" /> fires, or <see cref="FireOnceNow" /> to catch up with a single immediate fire.
/// </summary>
public enum SchedulerMisfirePolicy
{
    Smart = 0,
    SkipMissed = 1,
    FireOnceNow = 2
}
