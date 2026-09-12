namespace XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Runtime-supervision options for the whisper.cpp <c>whisper-server</c> daemon. The transcription runtime sits
///     beside the llama.cpp text runtime and the stable-diffusion.cpp image runtime and owns a <b>separate</b> loopback
///     port range, so the three supervisors never contend for a port.
/// </summary>
public sealed class WhisperRuntimeOptions
{
    public const string SectionName = "WhisperRuntime";

    /// <summary>Loopback host the daemon binds to. Always local — audio never leaves the node.</summary>
    public string ListenHost { get; set; } = "127.0.0.1";

    /// <summary>
    ///     Inclusive lower bound of the loopback port range the supervisor allocates a daemon from. Disjoint from
    ///     llama.cpp (18100–18199) and stable-diffusion.cpp (18200–18299).
    /// </summary>
    public int PortRangeStart { get; set; } = 18300;

    /// <summary>Inclusive upper bound of the loopback port range the supervisor allocates a daemon from.</summary>
    public int PortRangeEnd { get; set; } = 18399;

    /// <summary>
    ///     Idle time-to-live before the resident daemon is evicted to free its memory. Seeded from the operator's
    ///     <c>TranscriptionIdleTimeoutMinutes</c> node setting at host build.
    /// </summary>
    public TimeSpan IdleTimeToLive { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Budget for the whole readiness POLL LOOP after a spawn or an in-place model load.
    /// </summary>
    /// <remarks>
    ///     Flat, not scaled by model size: the largest catalogue row is 1.6 GB and the spike measured port-ready at
    ///     0.46 s for <c>base</c> on this box, so the image runtime's size-scaling (written for an 18 GB file-set) buys
    ///     nothing here.
    ///     <c>ponytail: flat 2-minute readiness budget; scale it if a real cold-cache load on a spinning disk ever times out.</c>
    /// </remarks>
    public TimeSpan ReadinessTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     Budget for ONE health probe. A bound daemon answers in microseconds, so a probe that takes longer is a
    ///     wedged daemon rather than a slow one. The readiness loop above is bounded separately.
    /// </summary>
    public TimeSpan HealthProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Budget for ONE in-place model load. The largest catalogue row is 1.6 GB.</summary>
    public TimeSpan ModelLoadTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Budget for ONE transcription request. A long file on a CPU backend legitimately takes minutes.</summary>
    public TimeSpan InferenceTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    ///     Minimum interval between reuse-path liveness probes of the resident daemon. Between probes a reuse is handed
    ///     out with no HTTP at all, so the hot path stays cheap.
    /// </summary>
    public TimeSpan ReuseLivenessProbeInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Bounds a single reuse-path liveness probe, so a daemon that accepts the connection but never answers cannot
    ///     stall the hot path. Exceeding it counts as a failed probe.
    /// </summary>
    public TimeSpan ReuseLivenessProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Number of <em>consecutive</em> failed reuse-path liveness probes after which a still-alive-but-unresponsive
    ///     daemon is torn down and respawned instead of being handed out again. One transient failure never evicts a
    ///     busy daemon; one success resets the count.
    /// </summary>
    public int MaxReuseLivenessFailures { get; set; } = 3;

    /// <summary>
    ///     Absolute path to the pinned Silero VAD weights, or <see langword="null" /> when they are not installed yet.
    ///     When null the launch omits both VAD flags rather than passing one of them.
    /// </summary>
    /// <remarks>
    ///     Seeded by the application layer from its model-path resolver. The provider never computes a node data path
    ///     itself, because the directory root is an application concern it must not reference.
    /// </remarks>
    public string? VadModelPath { get; set; }

    /// <summary>
    ///     Absolute path to the node's whisper models directory — the root the catalogue's relative
    ///     <c>{modelId}/{fileName}</c> path is resolved against. <see langword="null" /> until the application layer
    ///     seeds it, in which case the supervisor reports the model as not installed rather than guessing a location.
    /// </summary>
    /// <remarks>
    ///     Seeded alongside <see cref="VadModelPath" /> and for the same reason: the catalogue is a static table inside
    ///     this provider, but where the weights live is decided by the node data directory, which only the application
    ///     layer can resolve.
    /// </remarks>
    public string? ModelsDirectory { get; set; }
}
