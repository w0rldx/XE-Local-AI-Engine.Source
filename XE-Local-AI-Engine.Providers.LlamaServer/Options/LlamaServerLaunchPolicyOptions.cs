namespace XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Central, node-configurable launch policy for every <c>llama-server</c> spawn: the requested context window per
///     role, the GPU KV-cache quantization + flash-attention defaults, and the CPU thread policy. Consumed by
///     <see cref="XE_Local_AI_Engine.Providers.LlamaServer.Contracts.ILlamaServerLaunchPolicy" /> to produce the launch
///     decision the supervisor's launch-spec builder emits. Bound from node config at DI time.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Precedence (highest wins), enforced by the policy + the launch-spec builder:</strong> a frozen
///         inference profile (replayed verbatim — the policy never overrides a replay's <c>-c</c>/KV/FA) &gt; an
///         explicit per-send / user configuration &gt; these role defaults. These options only ever fill in what a
///         frozen profile or per-send override did not already pin.
///     </para>
///     <para>
///         The KV-cache quantization + flash-attention defaults apply to GPU builds only (CUDA/Vulkan). The CPU build
///         keeps f16 KV and auto flash-attention — quantized KV needs the fused flash-attention path, which is a GPU
///         win, not a CPU one.
///     </para>
/// </remarks>
public sealed class LlamaServerLaunchPolicyOptions
{
    /// <summary>
    ///     The chat-role context default (shared with the model advisor so its KV-fit math targets the same window the
    ///     runtime actually launches). Public so the Application-layer fit-estimator call sites reference ONE value.
    /// </summary>
    public const int DefaultChatContextTokens = 16384;

    /// <summary>
    ///     Requested chat-role context window in tokens (<c>-c</c>). Default <see cref="DefaultChatContextTokens" />
    ///     (16384): 2× the app-side conversation budget default (<c>ConversationContextBudgetOptions.DefaultContextTokens</c>
    ///     = 8192), leaving headroom for tool-call loops and the reserved output window, while staying modest in VRAM at
    ///     the 12–24 GB consumer-GPU target. Worked example — with q8_0 KV (1 byte/element) and a typical 8B model
    ///     (32 layers, GQA n_head_kv = 8, head_dim = 128): per-token KV = 2 × 32 × 8 × 128 × 1 B = 64 KiB, so 16384
    ///     tokens ≈ 1 GiB of KV cache — comfortably within budget. Was silently the model's full train context (e.g.
    ///     262144 ⇒ ~9 GB of KV+compute) before this policy, because no <c>-c</c> was emitted (AUD4-02). Must be positive.
    /// </summary>
    public int ChatContextTokens { get; init; } = DefaultChatContextTokens;

    /// <summary>
    ///     Requested embedding-role context window in tokens (<c>-c</c>). Default 2048 — embedding requests are single,
    ///     short forward passes (a chunk plus its prefix), so a large window only wastes KV allocation. Must be positive.
    /// </summary>
    public int EmbeddingContextTokens { get; init; } = DefaultEmbeddingContextTokens;

    /// <summary>
    ///     Requested reranker-role context window in tokens (<c>-c</c>). Default 2048 — a reranker scores short
    ///     (query, document) pairs one at a time, so it needs no more window than the embedding role. Must be positive.
    /// </summary>
    public int RerankerContextTokens { get; init; } = DefaultRerankerContextTokens;

    /// <summary>
    ///     Reserved headroom in tokens subtracted from a model's train-context ceiling when the requested role context
    ///     would otherwise be capped exactly at that ceiling. Requesting a model's absolute maximum context can fail to
    ///     allocate because llama.cpp reserves a little context internally for the chat template's special/system tokens,
    ///     so the launched window is capped at <c>trainContext − margin</c> (floored at 1). Only bites when the role
    ///     default exceeds the model's train context (e.g. a small model whose train context is below the chat default);
    ///     it never reduces a request that already fits. Relates to the budget math by keeping the launched window (and
    ///     therefore the propagated effective window both budgeters size against) a hair below the model's hard ceiling.
    ///     Must be non-negative.
    /// </summary>
    public int ContextSafetyMarginTokens { get; init; } = DefaultContextSafetyMarginTokens;

    /// <summary>
    ///     When set (the default), a GPU build (CUDA/Vulkan) launches with the fused flash-attention path and quantized
    ///     KV cache (<c>-fa on -ctk &lt;type&gt; -ctv &lt;type&gt;</c>), roughly halving KV-cache VRAM versus f16 — the
    ///     single biggest VRAM lever on 12–24 GB consumer GPUs (AUD4-05). A one-shot safe fallback (no <c>-ctk/-ctv</c>,
    ///     <c>-fa auto</c>) is recorded per backend if the optimized config fails to reach readiness, so a backend that
    ///     cannot serve it is never re-tried. Frozen profiles bypass this entirely (they pin their own KV/FA).
    /// </summary>
    public bool EnableGpuKvCacheQuantization { get; init; } = true;

    /// <summary>
    ///     The KV-cache key/value element type emitted for both <c>-ctk</c> and <c>-ctv</c> on a GPU build when
    ///     <see cref="EnableGpuKvCacheQuantization" /> is set. Default <c>q8_0</c> — 8-bit KV keeps quality effectively
    ///     lossless while halving KV bytes; it requires flash attention, which is emitted alongside it. Must be non-empty.
    /// </summary>
    public string KvCacheType { get; init; } = DefaultKvCacheType;

    /// <summary>
    ///     When set (the default), a CPU build emits an explicit thread policy (<c>-t</c>/<c>-tb</c>) derived from the
    ///     host's estimated physical-core count rather than letting llama.cpp pick. A GPU build never gets <c>-t</c>
    ///     (the compute runs on the GPU). AUD4-17: no thread flag was passed, so llama.cpp auto-selected a subset of the
    ///     logical cores.
    /// </summary>
    public bool EnableCpuThreadPolicy { get; init; } = true;

    /// <summary>
    ///     Whether to assume the host CPU uses simultaneous multithreading (SMT / Hyper-Threading), i.e. that
    ///     <see cref="System.Environment.ProcessorCount" /> reports twice the physical-core count. Default <c>true</c>
    ///     (the common x86 desktop case). When true the physical-core estimate is <c>logical / 2</c>; when false it is
    ///     the logical count. Only a heuristic — override <see cref="CpuThreadCount" />/<see cref="CpuThreadsBatchCount" />
    ///     to pin exact values on an atypical topology (e.g. hybrid P/E-core CPUs).
    /// </summary>
    public bool AssumeSimultaneousMultithreading { get; init; } = true;

    /// <summary>
    ///     Physical cores reserved for the host/app when deriving the generation thread count (<c>-t</c>) from the
    ///     physical-core estimate. Default 1 — leaves one core for Kestrel, the UI, and OS work so inference does not
    ///     starve the app that hosts it. The resulting <c>-t</c> is floored at 1. Must be non-negative.
    /// </summary>
    public int CpuThreadReserve { get; init; } = DefaultCpuThreadReserve;

    /// <summary>
    ///     Explicit generation thread count (<c>-t</c>) override. When set (&gt; 0) it wins over the physical-core
    ///     estimate; leave <see langword="null" /> to derive it as <c>physicalCores − <see cref="CpuThreadReserve" /></c>
    ///     (floored at 1). Only meaningful for the CPU build.
    /// </summary>
    public int? CpuThreadCount { get; init; }

    /// <summary>
    ///     Explicit prompt-batch thread count (<c>-tb</c>) override. When set (&gt; 0) it wins; leave
    ///     <see langword="null" /> to derive it as the full physical-core estimate (prompt processing parallelizes well,
    ///     so it uses every physical core, unlike the reserved generation count). Only meaningful for the CPU build.
    /// </summary>
    public int? CpuThreadsBatchCount { get; init; }

    private const int DefaultEmbeddingContextTokens = 2048;
    private const int DefaultRerankerContextTokens = 2048;
    private const int DefaultContextSafetyMarginTokens = 256;
    private const int DefaultCpuThreadReserve = 1;
    private const string DefaultKvCacheType = "q8_0";

    /// <summary>The role's requested context window in tokens (before capping to the model's train context).</summary>
    public int ContextTokensForRole(ModelRole role)
    {
        return role switch
        {
            ModelRole.Chat => ChatContextTokens,
            ModelRole.Embedding => EmbeddingContextTokens,
            ModelRole.Reranker => RerankerContextTokens,
            _ => ChatContextTokens
        };
    }

    /// <summary>
    ///     Fails fast on structurally invalid values so a misconfiguration surfaces at startup rather than as a spawn
    ///     that emits a nonsensical launch vector. Called by the launch policy's constructor.
    /// </summary>
    public void Validate()
    {
        if (ChatContextTokens <= 0)
        {
            throw new InvalidOperationException($"{nameof(ChatContextTokens)} must be positive (was {ChatContextTokens}).");
        }

        if (EmbeddingContextTokens <= 0)
        {
            throw new InvalidOperationException($"{nameof(EmbeddingContextTokens)} must be positive (was {EmbeddingContextTokens}).");
        }

        if (RerankerContextTokens <= 0)
        {
            throw new InvalidOperationException($"{nameof(RerankerContextTokens)} must be positive (was {RerankerContextTokens}).");
        }

        if (ContextSafetyMarginTokens < 0)
        {
            throw new InvalidOperationException($"{nameof(ContextSafetyMarginTokens)} must be non-negative (was {ContextSafetyMarginTokens}).");
        }

        if (string.IsNullOrWhiteSpace(KvCacheType))
        {
            throw new InvalidOperationException($"{nameof(KvCacheType)} must be non-empty.");
        }

        if (CpuThreadReserve < 0)
        {
            throw new InvalidOperationException($"{nameof(CpuThreadReserve)} must be non-negative (was {CpuThreadReserve}).");
        }

        if (CpuThreadCount is { } threads && threads <= 0)
        {
            throw new InvalidOperationException($"{nameof(CpuThreadCount)} must be positive when set (was {threads}).");
        }

        if (CpuThreadsBatchCount is { } batchThreads && batchThreads <= 0)
        {
            throw new InvalidOperationException($"{nameof(CpuThreadsBatchCount)} must be positive when set (was {batchThreads}).");
        }
    }
}
