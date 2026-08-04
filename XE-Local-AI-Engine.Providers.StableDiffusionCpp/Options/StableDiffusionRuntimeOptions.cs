namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Runtime-supervision options for the stable-diffusion.cpp <c>sd-server</c> daemon. The image runtime sits side by
///     side with the llama.cpp text runtime but owns a <b>separate</b> loopback port range so the two supervisors never
///     collide. This type owns the option shape; the sd-server runtime adapter wires the supervisor over it.
/// </summary>
public sealed class StableDiffusionRuntimeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "StableDiffusionRuntime";

    /// <summary>Loopback host the daemon binds to. Always local — image generation never leaves the node.</summary>
    public string ListenHost { get; set; } = "127.0.0.1";

    /// <summary>
    ///     Inclusive lower bound of the loopback port range the supervisor allocates a daemon from. Distinct from the
    ///     llama.cpp range (18100–18199) so the two runtimes never contend for a port.
    /// </summary>
    public int PortRangeStart { get; set; } = 18200;

    /// <summary>Inclusive upper bound of the loopback port range the supervisor allocates a daemon from.</summary>
    public int PortRangeEnd { get; set; } = 18299;

    /// <summary>Idle time-to-live before a resident daemon is evicted to free VRAM. Mirrors the llama.cpp idle reaper.</summary>
    public TimeSpan IdleTimeToLive { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Max number of concurrently-resident <c>sd-server</c> daemons before a spawn for a new model is rejected (an
    ///     idle least-recently-used daemon is evicted first to make room when possible). sd-server is VRAM-heavy and
    ///     typically co-resident with a chat model, so the default is a single daemon. Mirrors the llama.cpp loaded cap.
    /// </summary>
    public int MaxLoadedProcesses { get; set; } = 1;

    /// <summary>
    ///     Minimum interval between reuse-path liveness probes for a single daemon. A reuse is handed out immediately
    ///     (no HTTP) unless at least this long has passed since the last probe of that daemon, so the hot path stays
    ///     cheap: at most one capabilities probe per daemon per interval, not one per request. Mirrors the llama.cpp path.
    /// </summary>
    public TimeSpan ReuseLivenessProbeInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Number of <em>consecutive</em> failed reuse-path liveness probes after which a still-alive-but-unresponsive
    ///     (wedged) daemon is torn down and respawned instead of being handed out again. A single transient failure never
    ///     evicts a busy daemon; one successful probe resets the count.
    /// </summary>
    public int MaxReuseLivenessFailures { get; set; } = 3;

    /// <summary>
    ///     Bounds a single reuse-path liveness probe so a hung daemon (accepts the connection but never answers) cannot
    ///     stall the hot path for the whole <see cref="System.Net.Http.HttpClient" /> timeout; exceeding it counts as a
    ///     failed probe.
    /// </summary>
    public TimeSpan ReuseLivenessProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Readiness budget <b>floor</b>: the minimum time the supervisor waits for a freshly-spawned daemon's socket to
    ///     open (the daemon binds only after synchronous model load completes). A file-set large enough to need longer
    ///     than this gets a proportionally larger budget — see <see cref="ReadinessLoadBytesPerSecond" />.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Assumed worst-case load throughput, in bytes per second, used to scale the readiness budget to the size of the
    ///     file-set being loaded. A flat budget is only safe for the family it was measured on: SD1.5 is ~2 GB and loads
    ///     in seconds, while a Qwen-Image set is a diffusion transformer plus a 7B LLM text encoder plus a VAE — around
    ///     18 GB, with the text encoder pinned to CPU — and a flat two minutes would fail it on first launch with a
    ///     readiness timeout that looks like a broken model rather than an impatient budget. Deliberately pessimistic:
    ///     over-waiting costs nothing on the happy path (readiness is signalled the moment the socket opens), whereas
    ///     under-waiting kills a load that was going to succeed.
    /// </summary>
    public long ReadinessLoadBytesPerSecond { get; set; } = 40L * 1024 * 1024;

    /// <summary>
    ///     Absolute ceiling on the size-scaled readiness budget, so a corrupt or absurd registry size can never make a
    ///     failed spawn hang effectively forever.
    /// </summary>
    public TimeSpan MaxReadinessTimeout { get; set; } = TimeSpan.FromMinutes(30);
}
