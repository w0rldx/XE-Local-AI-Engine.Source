namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     How a scheduled job's trigger fires: a recurring <see cref="Cron" /> expression, a single <see cref="OneShot" />
///     fire, or a repeating fixed <see cref="SimpleInterval" />.
/// </summary>
public enum ScheduleKind
{
    Cron = 0,
    OneShot = 1,
    SimpleInterval = 2
}
