namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Shared eviction + port-allocation policy for the supervisor. One idle-TTL + loaded-cap + reaper
///     policy governs all <c>(model, role)</c> processes; chat and embedding processes both count against the cap.
///     Bound from node config at DI time.
/// </summary>
public sealed class LlamaServerSupervisorOptions
{
    /// <summary>Max number of concurrently-loaded <c>(model, role)</c> processes before spawn rejects.</summary>
    public int MaxLoadedProcesses { get; init; } = 3;

    /// <summary>Idle duration after which an unused process is evicted by the reaper.</summary>
    public TimeSpan IdleTimeToLive { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Inclusive lower bound of the localhost port range the supervisor allocates from.</summary>
    public int PortRangeStart { get; init; } = 18100;

    /// <summary>Inclusive upper bound of the localhost port range the supervisor allocates from.</summary>
    public int PortRangeEnd { get; init; } = 18199;

    /// <summary>Max consecutive crash-restarts for a single process before it surfaces a sanitized failure.</summary>
    public int MaxRestartAttempts { get; init; } = 3;

    /// <summary>
    ///     Minimum interval between reuse-path liveness probes for a single process. A reuse is handed out immediately
    ///     (no HTTP) unless at least this long has passed since the last probe of that process, so the hot path stays
    ///     cheap: at most one <c>/health</c> probe per process per interval, not one per request.
    /// </summary>
    public TimeSpan ReuseLivenessProbeInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Number of <em>consecutive</em> failed reuse-path liveness probes after which a still-alive-but-unresponsive
    ///     (wedged) process is torn down and respawned instead of being handed out again. A single transient failure never
    ///     evicts a busy server; one successful probe resets the count.
    /// </summary>
    public int MaxReuseLivenessFailures { get; init; } = 3;

    /// <summary>
    ///     Bounds a single reuse-path liveness probe so a hung server (accepts the connection but never answers) cannot
    ///     stall the hot path for the whole <see cref="System.Net.Http.HttpClient" /> timeout; exceeding it counts as a
    ///     failed probe.
    /// </summary>
    public TimeSpan ReuseLivenessProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);
}
