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
    ///     llama-server chat-role prompt-cache prefix-reuse window in tokens (<c>--cache-reuse N</c>). <c>0</c> disables
    ///     it (the upstream default); <c>256</c> is the recommended chat/agent value.
    /// </summary>
    /// <remarks>
    ///     Chat role only — an embedding server does one-shot forward passes with no shared prefix to reuse. The flag is
    ///     emitted whatever the profile source (explore or frozen replay) and is not part of any frozen-profile
    ///     identity, so changing it never invalidates a stored profile; it takes effect on the next natural (re)spawn.
    ///     What the reuse buys: docs/wiki/03-local-runtime-and-providers.md, "Per-role launch flags and the pooled
    ///     batch-size rule".
    /// </remarks>
    public int ChatCacheReuse { get; init; } = 256;

    /// <summary>
    ///     llama-server chat-role host-RAM prompt-cache budget in MiB (<c>--cache-ram N</c>); <c>0</c> disables the host
    ///     prompt cache. Must be non-negative. Defaults via <see cref="ComputeDefaultChatCacheRamMiB" />.
    /// </summary>
    /// <remarks>
    ///     The supervisor always emits the flag explicitly rather than inherit the pinned build's implicit 8192 MiB
    ///     default, whose eviction is known-ineffective on Linux under default overcommit (upstream issue #22629). This
    ///     budget goes to chat; the pooled embedding/rerank roles get <c>0</c>, having no prompt state worth caching.
    ///     Like <see cref="ChatCacheReuse" /> it is a launch flag outside any frozen-profile identity. See
    ///     docs/wiki/03-local-runtime-and-providers.md, "Per-role launch flags and the pooled batch-size rule".
    /// </remarks>
    public int ChatCacheRamMiB { get; init; } = ComputeDefaultChatCacheRamMiB(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>
    ///     Detected-RAM default for <see cref="ChatCacheRamMiB" />: one eighth of total available memory, clamped to
    ///     [512, 8192] MiB.
    /// </summary>
    /// <remarks>
    ///     That is 2048 MiB on a 16 GB machine, 4096 on 32 GB, and upstream's 8192 default only at 64 GB and above. An
    ///     unknown or non-positive total yields the conservative floor.
    /// </remarks>
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
    ///     Chat-role speculative-decoding <c>--spec-type</c>, shipping
    ///     <see cref="SpeculativeDecodingSettings.DisabledMode" /> (<c>none</c>) — operator opt-in. Validated against the
    ///     pinned build's accepted set.
    /// </summary>
    /// <remarks>
    ///     Only the external-draft modes (see <see cref="SpeculativeModeClass" />) also need a draft model;
    ///     <c>draft-mtp</c> drafts from heads in the main model and <c>ngram-*</c> modes self-speculate from context.
    ///     Chat role only — an embedding server has nothing to draft — and, like <see cref="ChatCacheReuse" />, a launch
    ///     flag independent of any frozen inference profile. Per-mode flags:
    ///     docs/wiki/03-local-runtime-and-providers.md, "Per-role launch flags and the pooled batch-size rule".
    /// </remarks>
    public string SpeculativeMode { get; init; } = SpeculativeDecodingSettings.DisabledMode;

    /// <summary>
    ///     Installed draft model NAME for external-draft speculative modes, so the operator UI can offer installed model
    ///     names without knowing file paths. Ignored by every other <see cref="SpeculativeModeClass" />.
    /// </summary>
    /// <remarks>
    ///     Resolved to its on-disk GGUF on the spawn path via
    ///     <see cref="XE_Local_AI_Engine.Providers.Abstractions.Gguf.IGgufModelStore.ResolveModelFilePathAsync" /> — the
    ///     same resolution the target model uses. When <see cref="SpeculativeDraftModelPath" /> is also set, the explicit
    ///     path wins and this name is not resolved.
    /// </remarks>
    public string? SpeculativeDraftModelName { get; init; }

    /// <summary>
    ///     Explicit path to the draft GGUF for external-draft speculative modes (it must share the target model's
    ///     tokenizer family). An escape hatch taking precedence over <see cref="SpeculativeDraftModelName" />.
    /// </summary>
    /// <remarks>
    ///     Normally left unset so the name is resolved on the spawn path; ignored by every other
    ///     <see cref="SpeculativeModeClass" />. The draft model loads inside the chat process and is never separately
    ///     ledgered or footprint-estimated, which is why a non-NVIDIA profile with no free-VRAM reading rejects an
    ///     external-draft admission rather than undercount it. See docs/wiki/03-local-runtime-and-providers.md,
    ///     "Per-role launch flags and the pooled batch-size rule".
    /// </remarks>
    public string? SpeculativeDraftModelPath { get; init; }

    /// <summary>
    ///     Draft tokens proposed per step (<c>--spec-draft-n-max</c>, upstream default 3). <c>0</c> omits the flag.
    ///     Honoured by external-draft AND <c>draft-mtp</c> modes; <c>ngram-*</c> modes size their drafts from their own
    ///     <c>--spec-ngram-*</c> knobs instead.
    /// </summary>
    public int SpeculativeDraftMaxTokens { get; init; } = 3;

    /// <summary>
    ///     GPU layers to offload for the draft model (<c>--spec-draft-ngl</c>). <c>null</c> omits the flag (draft model
    ///     placement left to the runtime default). Only meaningful for external-draft modes — it sizes a draft-model load,
    ///     which <c>draft-mtp</c> never performs.
    /// </summary>
    public int? SpeculativeDraftGpuLayers { get; init; }

    /// <summary>Validated speculative-decoding bundle assembled from the four <c>Speculative*</c> keys for the launch path.</summary>
    public SpeculativeDecodingSettings Speculative => new(SpeculativeMode, SpeculativeDraftModelPath, SpeculativeDraftMaxTokens, SpeculativeDraftGpuLayers);

    /// <summary>
    ///     Minimum interval between reuse-path <c>/health</c> liveness probes of one process: a reuse inside the window
    ///     is handed out with no HTTP call at all, so the hot path costs at most one probe per process per interval.
    /// </summary>
    public TimeSpan ReuseLivenessProbeInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Number of <em>consecutive</em> failed reuse-path liveness probes after which a wedged (alive but
    ///     unresponsive) process is torn down and respawned. One successful probe resets the count, so a single
    ///     transient failure never evicts a busy server.
    /// </summary>
    public int MaxReuseLivenessFailures { get; init; } = 3;

    /// <summary>
    ///     Bounds a single reuse-path liveness probe so a hung server (accepts the connection but never answers) cannot
    ///     stall the hot path for the whole <see cref="System.Net.Http.HttpClient" /> timeout; exceeding it counts as a
    ///     failed probe.
    /// </summary>
    public TimeSpan ReuseLivenessProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Base cold-start readiness budget for a freshly spawned process — the FLOOR of the size-aware readiness
    ///     deadline, which a larger model extends via <see cref="ReadinessTimeoutSecondsPerGiB" />. Must be positive.
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
    ///     <see cref="ReadinessTimeoutModelSizeThresholdGiB" />, so a deterministically slow large model on a cold
    ///     cache is not killed and retried before it can finish loading. Must be non-negative.
    /// </summary>
    public double ReadinessTimeoutSecondsPerGiB { get; init; } = DefaultReadinessSecondsPerGiB;

    /// <summary>
    ///     Hard ceiling on the size-aware readiness deadline, bounding the worst-case stall: however large the model, a
    ///     spawn never gets more than this. Must be positive and at least <see cref="ReadinessBaseTimeout" />.
    /// </summary>
    public TimeSpan ReadinessTimeoutCap { get; init; } = TimeSpan.FromSeconds(DefaultReadinessCapSeconds);

    /// <summary>
    ///     How many times a spawn that TIMED OUT waiting for readiness (alive but slow) is retried before the failure is
    ///     surfaced. Must be non-negative.
    /// </summary>
    /// <remarks>
    ///     A readiness timeout on a deterministically slow or large model is not a transient crash, so retrying it many
    ///     times only multiplies the kill/reload thrash; the default retries it at most once. A process exit during load
    ///     — a deterministic crash — stays non-retryable regardless of this value, and a transient start failure is
    ///     still retried up to <see cref="MaxRestartAttempts" />.
    /// </remarks>
    public int MaxReadinessTimeoutRetries { get; init; } = DefaultMaxReadinessTimeoutRetries;

    /// <summary>
    ///     Bounded time a graceful operator eject — which marks the process evicting, taking no new leases — waits for
    ///     in-flight inference to drain before tearing the process down. Must be positive.
    /// </summary>
    /// <remarks>
    ///     If the wait elapses the eject reports that it could not complete safely, unless the caller forced it.
    /// </remarks>
    public TimeSpan EjectDrainTimeout { get; init; } = TimeSpan.FromSeconds(DefaultEjectDrainTimeoutSeconds);

    /// <summary>
    ///     Network timeout for a single <em>chat</em> call to the llama-server OpenAI-compatible surface. Must be
    ///     positive.
    /// </summary>
    /// <remarks>
    ///     Set EXPLICITLY so it never inherits System.ClientModel's 100 s <c>NetworkTimeout</c> default, and GENEROUS on
    ///     purpose: the stream-idle watchdog and the invocation deadline already bound a turn, so this is only the
    ///     outermost floor against a wedged socket and must not pre-empt a slow-but-progressing local model. The SDK
    ///     retry layer is pinned OFF independently of it. See docs/wiki/03-local-runtime-and-providers.md,
    ///     "Reuse-path liveness, retry classes and the two HTTP network floors".
    /// </remarks>
    public TimeSpan HttpNetworkTimeout { get; init; } = TimeSpan.FromSeconds(DefaultHttpNetworkTimeoutSeconds);

    /// <summary>
    ///     Network timeout for a single <em>embedding</em> call to the same surface. Deliberately SHORTER than
    ///     <see cref="HttpNetworkTimeout" /> (600 s vs 3600 s). Must be positive.
    /// </summary>
    /// <remarks>
    ///     A chat call carries the invocation deadline's cancellation token, so its HTTP timeout may be generous. An
    ///     embedding call (knowledge ingestion, memory extraction) carries no such per-request deadline — this value IS
    ///     its only bound, so a wedged embedding request must fail in minutes, not an hour.
    /// </remarks>
    public TimeSpan EmbeddingHttpNetworkTimeout { get; init; } = TimeSpan.FromSeconds(DefaultEmbeddingHttpNetworkTimeoutSeconds);

    private const double DefaultReadinessBaseTimeoutSeconds = 120d;
    private const double DefaultReadinessSizeThresholdGiB = 4d;
    private const double DefaultReadinessSecondsPerGiB = 20d;
    private const double DefaultReadinessCapSeconds = 600d;
    private const int DefaultMaxReadinessTimeoutRetries = 1;

    private const double DefaultEjectDrainTimeoutSeconds = 30d;

    // COUPLING, kept in step by hand: StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds (3600d) is the ceiling of the node-level "Maximum
    // message request timeout" and unreachable from here; a shorter default hands an operator who raised it an inner socket abort first.
    private const double DefaultHttpNetworkTimeoutSeconds = 3600d;

    // Shorter than the chat floor on purpose: only the chat path carries the invocation-deadline token that makes a
    // one-hour floor safe, so the embedding floor stays the value that bounds a wedged request in minutes.
    private const double DefaultEmbeddingHttpNetworkTimeoutSeconds = 600d;

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

        if (EmbeddingHttpNetworkTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(EmbeddingHttpNetworkTimeout)} must be positive (was {EmbeddingHttpNetworkTimeout}).");
        }

        if (ChatCacheRamMiB < 0)
        {
            throw new InvalidOperationException($"{nameof(ChatCacheRamMiB)} must be non-negative (was {ChatCacheRamMiB}).");
        }
    }
}
