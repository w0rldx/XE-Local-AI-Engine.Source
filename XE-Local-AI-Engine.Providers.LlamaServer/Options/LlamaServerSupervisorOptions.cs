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
    ///     llama-server chat-role host-RAM prompt-cache budget in MiB (<c>--cache-ram N</c>). The pinned build's
    ///     upstream default is 8192 MiB — half the physical RAM of a 16 GB machine — inherited silently when the flag
    ///     is omitted, and its eviction is known-ineffective on Linux under default overcommit (the OOM killer fires
    ///     before <c>std::bad_alloc</c> does; upstream issue #22629). The supervisor therefore always emits the flag
    ///     explicitly: this budget for chat, <c>0</c> (disabled) for pooled embedding/rerank roles, which do one-shot
    ///     forward passes with no prompt state worth caching. Defaults to a detected-RAM-proportional value via
    ///     <see cref="ComputeDefaultChatCacheRamMiB" />; <c>0</c> disables the host prompt cache entirely. Like
    ///     <see cref="ChatCacheReuse" />, it is a launch flag outside any frozen-profile identity.
    /// </summary>
    public int ChatCacheRamMiB { get; init; } = ComputeDefaultChatCacheRamMiB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    ///     Detected-RAM default for <see cref="ChatCacheRamMiB" />: one eighth of total available memory, clamped to
    ///     [512, 8192] MiB — 2048 on a 16 GB machine, 4096 on 32 GB, the upstream default 8192 only at 64 GB+. An
    ///     unknown/non-positive total yields the conservative floor.
    /// </summary>
    public static int ComputeDefaultChatCacheRamMiB(long totalAvailableMemoryBytes)
    {
        const int FloorMiB = 512;
        const int CeilingMiB = 8192;
        if (totalAvailableMemoryBytes <= 0)
        {
            return FloorMiB;
        }

        var oneEighthMiB = totalAvailableMemoryBytes / (8L * 1024L * 1024L);
        return (int)Math.Clamp(oneEighthMiB, FloorMiB, CeilingMiB);
    }

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

    /// <summary>
    ///     Base cold-start readiness budget for a freshly spawned process before its first request (the floor of the
    ///     size-aware readiness deadline). A small model gets exactly this; a larger one gets this plus a size-aware
    ///     extension (see <see cref="ReadinessTimeoutSecondsPerGiB" />), so a deterministically slow big model is not
    ///     killed and retried before it can finish loading. Must be positive.
    /// </summary>
    public TimeSpan ReadinessBaseTimeout { get; init; } = TimeSpan.FromSeconds(DefaultReadinessBaseTimeoutSeconds);

    /// <summary>
    ///     On-disk model size (GiB) above which the readiness deadline is extended. At or below this the base timeout is
    ///     used unchanged; above it, <see cref="ReadinessTimeoutSecondsPerGiB" /> seconds are added per excess GiB. Must
    ///     be non-negative.
    /// </summary>
    public double ReadinessTimeoutModelSizeThresholdGiB { get; init; } = DefaultReadinessSizeThresholdGiB;

    /// <summary>
    ///     Seconds of readiness budget added per GiB of on-disk model size ABOVE
    ///     <see cref="ReadinessTimeoutModelSizeThresholdGiB" />. A large model on a cold cache/slow disk loads
    ///     proportionally slower, so its readiness deadline scales with its size instead of a one-size-fits-all constant.
    ///     Must be non-negative.
    /// </summary>
    public double ReadinessTimeoutSecondsPerGiB { get; init; } = DefaultReadinessSecondsPerGiB;

    /// <summary>
    ///     Hard ceiling on the size-aware readiness deadline: no matter how large the model, a spawn is never given more
    ///     than this to become ready before it is treated as a readiness timeout. Bounds the worst-case stall. Must be
    ///     positive and at least <see cref="ReadinessBaseTimeout" />.
    /// </summary>
    public TimeSpan ReadinessTimeoutCap { get; init; } = TimeSpan.FromSeconds(DefaultReadinessCapSeconds);

    /// <summary>
    ///     How many times a spawn that TIMED OUT waiting for readiness (the process is alive but slow) is retried before
    ///     the failure is surfaced. A readiness timeout on a deterministically slow/large model is not a transient crash,
    ///     so retrying it many times only multiplies the kill/reload thrash; the default retries it at most once. A
    ///     process-exit-during-load (a deterministic crash) stays non-retryable regardless of this value, and a transient
    ///     start failure is still retried up to <see cref="MaxRestartAttempts" />. Must be non-negative.
    /// </summary>
    public int MaxReadinessTimeoutRetries { get; init; } = DefaultMaxReadinessTimeoutRetries;

    /// <summary>
    ///     Bounded time an operator eject waits for in-flight inference to drain before it reports back. A graceful eject
    ///     marks the process evicting (no new leases), waits up to this for active requests to finish, then tears the
    ///     process down; if the wait elapses the eject reports it could not complete safely (unless forced). Must be
    ///     positive.
    /// </summary>
    public TimeSpan EjectDrainTimeout { get; init; } = TimeSpan.FromSeconds(DefaultEjectDrainTimeoutSeconds);

    /// <summary>
    ///     Network timeout for a single local-inference HTTP call to the llama-server OpenAI-compatible surface (AUD4-18).
    ///     Set EXPLICITLY on the built OpenAI client so it never inherits System.ClientModel's 100 s
    ///     <c>NetworkTimeout</c> default (which would abort a legitimately long local generation). Deliberately GENEROUS
    ///     (default 600 s): streaming inter-token stalls are already bounded by the invocation's stream-idle watchdog and
    ///     a non-streaming sub-agent completion is bounded by the invocation timeout, so this is only the outermost floor
    ///     against a wedged socket and must not pre-empt a slow-but-progressing local model. The SDK retry layer is pinned
    ///     OFF independently of this value (a local chat completion is non-idempotent and must never be re-issued). Must be
    ///     positive.
    /// </summary>
    public TimeSpan HttpNetworkTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpNetworkTimeoutSeconds);

    private const double DefaultReadinessBaseTimeoutSeconds = 120d;
    private const double DefaultReadinessSizeThresholdGiB = 4d;
    private const double DefaultReadinessSecondsPerGiB = 20d;
    private const double DefaultReadinessCapSeconds = 600d;
    private const int DefaultMaxReadinessTimeoutRetries = 1;
    private const double DefaultEjectDrainTimeoutSeconds = 30d;
    private const double DefaultHttpNetworkTimeoutSeconds = 600d;

    /// <summary>
    ///     Computes the size-aware cold-start readiness deadline for a model of <paramref name="modelSizeBytes" /> on
    ///     disk: <see cref="ReadinessBaseTimeout" /> plus <see cref="ReadinessTimeoutSecondsPerGiB" /> per GiB above
    ///     <see cref="ReadinessTimeoutModelSizeThresholdGiB" />, clamped to <see cref="ReadinessTimeoutCap" />. A
    ///     non-positive/unknown size (0) yields the base timeout unchanged.
    /// </summary>
    public TimeSpan ResolveReadinessTimeout(long modelSizeBytes)
    {
        if (modelSizeBytes <= 0)
        {
            return ReadinessBaseTimeout;
        }

        const double BytesPerGiB = 1024d * 1024d * 1024d;
        var sizeGiB = modelSizeBytes / BytesPerGiB;
        var excessGiB = Math.Max(0d, sizeGiB - ReadinessTimeoutModelSizeThresholdGiB);
        var extended = ReadinessBaseTimeout + TimeSpan.FromSeconds(excessGiB * ReadinessTimeoutSecondsPerGiB);

        return extended > ReadinessTimeoutCap ? ReadinessTimeoutCap : extended;
    }

    /// <summary>
    ///     Fails fast on structurally invalid values so a misconfiguration surfaces at startup rather than as a runtime
    ///     stall. Called by the supervisor's constructor.
    /// </summary>
    public void Validate()
    {
        if (ReadinessBaseTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(ReadinessBaseTimeout)} must be positive (was {ReadinessBaseTimeout}).");
        }

        if (ReadinessTimeoutCap <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(ReadinessTimeoutCap)} must be positive (was {ReadinessTimeoutCap}).");
        }

        if (ReadinessTimeoutCap < ReadinessBaseTimeout)
        {
            throw new InvalidOperationException($"{nameof(ReadinessTimeoutCap)} ({ReadinessTimeoutCap}) must be at least {nameof(ReadinessBaseTimeout)} ({ReadinessBaseTimeout}).");
        }

        if (ReadinessTimeoutModelSizeThresholdGiB < 0d)
        {
            throw new InvalidOperationException($"{nameof(ReadinessTimeoutModelSizeThresholdGiB)} must be non-negative (was {ReadinessTimeoutModelSizeThresholdGiB}).");
        }

        if (ReadinessTimeoutSecondsPerGiB < 0d)
        {
            throw new InvalidOperationException($"{nameof(ReadinessTimeoutSecondsPerGiB)} must be non-negative (was {ReadinessTimeoutSecondsPerGiB}).");
        }

        if (MaxReadinessTimeoutRetries < 0)
        {
            throw new InvalidOperationException($"{nameof(MaxReadinessTimeoutRetries)} must be non-negative (was {MaxReadinessTimeoutRetries}).");
        }

        if (EjectDrainTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(EjectDrainTimeout)} must be positive (was {EjectDrainTimeout}).");
        }

        if (HttpNetworkTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(HttpNetworkTimeout)} must be positive (was {HttpNetworkTimeout}).");
        }

        if (ChatCacheRamMiB < 0)
        {
            throw new InvalidOperationException($"{nameof(ChatCacheRamMiB)} must be non-negative (was {ChatCacheRamMiB}).");
        }
    }
}
