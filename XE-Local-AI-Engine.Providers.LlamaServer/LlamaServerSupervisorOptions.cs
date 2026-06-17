namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Shared eviction + port-allocation policy for the supervisor (decision #15). One idle-TTL + loaded-cap + reaper
///     policy governs all <c>(model, role)</c> processes; chat and embedding processes both count against the cap
///     (decision #16). Bound from node config at DI time (T4).
/// </summary>
public sealed class LlamaServerSupervisorOptions
{
    /// <summary>Max number of concurrently-loaded <c>(model, role)</c> processes before spawn rejects (decision #18).</summary>
    public int MaxLoadedProcesses { get; init; } = 3;

    /// <summary>Idle duration after which an unused process is evicted by the reaper.</summary>
    public TimeSpan IdleTimeToLive { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Inclusive lower bound of the localhost port range the supervisor allocates from.</summary>
    public int PortRangeStart { get; init; } = 18100;

    /// <summary>Inclusive upper bound of the localhost port range the supervisor allocates from.</summary>
    public int PortRangeEnd { get; init; } = 18199;

    /// <summary>Max consecutive crash-restarts for a single process before it surfaces a sanitized failure.</summary>
    public int MaxRestartAttempts { get; init; } = 3;
}
