namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Severity of a scheduled job run event: <see cref="Info" />, <see cref="Warning" />, <see cref="Error" />, or a
///     <see cref="Progress" /> heartbeat.
/// </summary>
public enum ScheduledRunEventLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
    Progress = 3
}
