namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     How a scheduled job's trigger fires: a recurring <see cref="Cron" /> expression, a single <see cref="OneShot" />
///     fire, a repeating fixed <see cref="SimpleInterval" />, or a <see cref="Manual" /> on-demand job.
/// </summary>
public enum ScheduleKind
{
    Cron = 0,
    OneShot = 1,
    SimpleInterval = 2,

    /// <summary>
    ///     A durable on-demand job with no trigger; fired only by <c>TriggerNowAsync</c>, never auto-fires. The job
    ///     detail is stored durably (so no trigger is required) and stays registered until disabled/deleted. Stored as
    ///     the int <c>3</c>, so this additive value needs no migration and leaves existing rows unaffected.
    /// </summary>
    Manual = 3
}
