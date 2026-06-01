namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     What caused a scheduled job run to fire: the <see cref="Schedule" /> itself, a <see cref="Manual" /> operator
///     action, an <see cref="Agent" />, or the <see cref="System" /> (e.g. startup reconciliation).
/// </summary>
public enum ScheduledRunTrigger
{
    Schedule = 0,
    Manual = 1,
    Agent = 2,
    System = 3
}
