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
    ///     llama-server chat-role prompt-cache prefix-reuse window in tokens (<c>--cache-reuse N</c>). Lets the server
    ///     reuse an unchanged prompt prefix via KV cache shifting even when a later part of the prompt changes, so a
    ///     multi-turn chat/agent conversation — which resends the full selected-path history every turn — skips
    ///     reprocessing the prefix and returns the first token sooner. Applies to the chat role only; an embedding
    ///     server does one-shot forward passes with no shared prefix to reuse. <c>0</c> disables it (upstream default);
    ///     <c>256</c> is the recommended chat/agent value. The flag is emitted regardless of profile source (explore or
    ///     frozen replay) and is not part of any frozen-profile identity, so changing it never invalidates a stored
    ///     profile — it only takes effect on the next natural (re)spawn of the process.
    /// </summary>
    public int ChatCacheReuse { get; init; } = 256;

    /// <summary>
    ///     Chat-role speculative-decoding <c>--spec-type</c>. Ships <see cref="SpeculativeDecodingSettings.DisabledMode" />
    ///     (<c>none</c>, off) — operator opt-in. Validated against the pinned build's accepted set; <c>draft-*</c> modes
    ///     also need <see cref="SpeculativeDraftModelPath" />, <c>ngram-*</c> modes self-speculate with no draft model.
    ///     Applies to the chat role only (embedding servers have nothing to draft) and, like <see cref="ChatCacheReuse" />,
    ///     is a launch flag independent of any frozen inference profile — changing it never invalidates a stored profile.
    /// </summary>
    public string SpeculativeMode { get; init; } = SpeculativeDecodingSettings.DisabledMode;

    /// <summary>
    ///     Installed draft model NAME for <c>draft-*</c> speculative modes, resolved to its on-disk GGUF on the spawn path
    ///     via <see cref="XE_Local_AI_Engine.Providers.Abstractions.Gguf.IGgufModelStore.ResolveModelFilePathAsync" /> —
    ///     the same resolution the target model uses — so the operator UI can offer installed model names without knowing
    ///     file paths. Ignored by <c>ngram-*</c> modes. When <see cref="SpeculativeDraftModelPath" /> is also set, the
    ///     explicit path wins and this name is not resolved.
    /// </summary>
    public string? SpeculativeDraftModelName { get; init; }

    /// <summary>
    ///     Explicit path to the draft GGUF for <c>draft-*</c> speculative modes (must share the target model's tokenizer
    ///     family). An escape hatch that takes precedence over <see cref="SpeculativeDraftModelName" /> when set; normally
    ///     left unset so the name is resolved on the spawn path. Ignored by <c>ngram-*</c> modes. The draft model loads
    ///     inside the chat process and is never separately ledgered or footprint-estimated; on the primary NVIDIA path its
    ///     resident VRAM is still reflected in <c>CapacityService</c>'s free-VRAM baseline (<c>nvidia-smi memory.free</c>),
    ///     but on the non-NVIDIA total-minus-ledger fallback it stays uncounted (see supervisor spawn path).
    /// </summary>
    public string? SpeculativeDraftModelPath { get; init; }

    /// <summary>
    ///     Draft tokens proposed per step (<c>--spec-draft-n-max</c>, upstream default 3). <c>0</c> omits the flag.
    ///     Only meaningful for <c>draft-*</c> modes.
    /// </summary>
    public int SpeculativeDraftMaxTokens { get; init; } = 3;

    /// <summary>
    ///     GPU layers to offload for the draft model (<c>--spec-draft-ngl</c>). <c>null</c> omits the flag (draft model
    ///     placement left to the runtime default). Only meaningful for <c>draft-*</c> modes.
    /// </summary>
    public int? SpeculativeDraftGpuLayers { get; init; }

    /// <summary>Validated speculative-decoding bundle assembled from the four <c>Speculative*</c> keys for the launch path.</summary>
    public SpeculativeDecodingSettings Speculative => new(SpeculativeMode, SpeculativeDraftModelPath, SpeculativeDraftMaxTokens, SpeculativeDraftGpuLayers);

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
