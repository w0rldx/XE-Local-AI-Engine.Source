namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Runtime-supervision options for the stable-diffusion.cpp <c>sd-server</c> daemon. The image runtime sits side by
///     side with the llama.cpp text runtime but owns a <b>separate</b> loopback port range so the two supervisors never
///     collide (architecture invariant §3). Lane A owns the option shape; Lane B wires the supervisor over it.
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
    ///     Readiness budget: the maximum time the supervisor waits for a freshly-spawned daemon's socket to open (the
    ///     daemon binds only after synchronous model load completes; §4A). Exceeding it is a load failure.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
