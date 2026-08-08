namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Lifecycle state of a single model-fit snapshot run, from <see cref="Queued" /> through a terminal outcome
///     (<see cref="Succeeded" />, <see cref="Failed" />, <see cref="Cancelled" /> or <see cref="TimedOut" />). The
///     numeric values are persisted, so existing values must never be renumbered.
/// </summary>
public enum ModelFitRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
    TimedOut = 5
}
