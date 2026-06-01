namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Who created a scheduled job definition: a <see cref="User" />, an <see cref="Agent" />, or the
///     <see cref="System" />.
/// </summary>
public enum ScheduledJobCreator
{
    User = 0,
    Agent = 1,
    System = 2
}
