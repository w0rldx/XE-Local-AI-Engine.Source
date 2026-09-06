namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The resolved llama-server launch-argument decision for one <c>(model, role, backend)</c> spawn, produced by an
///     <see cref="IInferenceProfileResolver" /> and consumed by the supervisor's single launch-spec builder. Two modes:
///     <see cref="ExploreMode" /> (let llama.cpp auto-fit choose placement) versus replay
///     (a frozen/explored profile whose explicit <c>-c/-ngl/-ts/-ot/-ctk/-ctv</c> args are emitted verbatim).
/// </summary>
/// <remarks>
///     <para>
///         The two modes are mutually exclusive per run because any explicit fit-arg DISABLES llama.cpp auto-fit
///         (verified against release <c>b9692</c>; the pinned <c>b10201</c> <c>--help</c> confirms <c>--fit</c> adjusts
///         only UNSET arguments): explore writes no explicit args so auto-fit runs; replay writes the
///         frozen args and omits <c>--fit</c>. Re-exploring is the only path back to auto-fit.
///     </para>
///     <para>
///         Replay invariants (enforced by <see cref="Replay" />): the KV cache types are both set or both null
///         <strong>and identical</strong> (the fused flash-attention path requires matching K/V types), and
///         <see cref="FlashAttn" /> must be enabled whenever the KV cache types are set (quantized/explicit KV
///         requires flash attention).
///     </para>
/// </remarks>
public sealed record ResolvedLaunchArguments
{
    private ResolvedLaunchArguments()
    {
    }

    /// <summary>
    ///     When <see langword="true" />, the supervisor emits <c>--fit on</c> + <c>--metrics</c> and NO explicit
    ///     fit-arg, letting llama.cpp choose and print placement. All replay fields are ignored in this mode.
    /// </summary>
    public bool ExploreMode { get; private init; }

    /// <summary>Frozen context size (<c>-c</c>). Replay mode only.</summary>
    public int CtxSize { get; private init; }

    /// <summary>
    ///     Explore mode: an optional request-scoped context-window override that pins this explore spawn's <c>-c</c>.
    ///     <see langword="null" /> (the default, and always the case for a replay) leaves the allocation resolver's
    ///     hardware-tier choice untouched, so a null carries exactly the behaviour that existed before this field.
    ///     Never persisted and never bound from configuration; it lives for the one spawn it was passed to.
    /// </summary>
    public int? ExploreContextTokensOverride { get; private init; }

    /// <summary>Frozen GPU layer count (<c>--n-gpu-layers</c>); <see langword="null" /> leaves it unset. Replay only.</summary>
    public int? NGpuLayers { get; private init; }

    /// <summary>Frozen tensor split (<c>-ts</c>); <see langword="null" /> leaves it unset. Replay only.</summary>
    public string? TensorSplit { get; private init; }

    /// <summary>Frozen expert/tensor placement (<c>-ot</c>); <see langword="null" /> leaves it unset. Replay only.</summary>
    public string? OverrideTensor { get; private init; }

    /// <summary>Frozen KV cache key type (<c>-ctk</c>); set together with <see cref="KvTypeV" />. Replay only.</summary>
    public string? KvTypeK { get; private init; }

    /// <summary>Frozen KV cache value type (<c>-ctv</c>); set together with <see cref="KvTypeK" />. Replay only.</summary>
    public string? KvTypeV { get; private init; }

    /// <summary>Whether the fused flash-attention path is enabled (<c>--flash-attn on</c>). Replay only.</summary>
    public bool FlashAttn { get; private init; }

    /// <summary>
    ///     Explore mode: the supervisor emits <c>--fit on</c> + <c>--metrics</c> so llama.cpp auto-fits placement and
    ///     obtains the fitted replay arguments separately from <c>llama-fit-params</c>. The default a self-satisfying
    ///     resolver returns when no profile exists.
    /// </summary>
    /// <param name="contextTokensOverride">
    ///     An optional operator-supplied context window for this explore only (see
    ///     <see cref="ExploreContextTokensOverride" />). Omitted/<see langword="null" /> is the default hardware-tier
    ///     behaviour.
    /// </param>
    public static ResolvedLaunchArguments Explore(int? contextTokensOverride = null)
    {
        return new ResolvedLaunchArguments
        {
            ExploreMode = true,
            ExploreContextTokensOverride = contextTokensOverride
        };
    }

    /// <summary>
    ///     Replay mode: a frozen/explored profile whose explicit launch args are emitted verbatim (no <c>--fit</c>).
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     Thrown when only one KV cache type is supplied (matching-type rule) or when KV cache types are supplied
    ///     without <paramref name="flashAttn" /> (quantized/explicit KV requires the fused flash-attention path).
    /// </exception>
    public static ResolvedLaunchArguments Replay(int ctxSize,
        int? nGpuLayers = null,
        string? tensorSplit = null,
        string? overrideTensor = null,
        string? kvTypeK = null,
        string? kvTypeV = null,
        bool flashAttn = false)
    {
        var kvKeySet = !string.IsNullOrWhiteSpace(kvTypeK);
        var kvValueSet = !string.IsNullOrWhiteSpace(kvTypeV);
        if (kvKeySet != kvValueSet)
        {
            throw new ArgumentException("KV cache types must be both set or both null (the fused flash-attention path requires matching K/V types).",
                kvKeySet ? nameof(kvTypeV) : nameof(kvTypeK));
        }

        if (kvKeySet && !string.Equals(kvTypeK, kvTypeV, StringComparison.Ordinal))
        {
            throw new ArgumentException("KV cache types must match (the fused flash-attention path requires identical K/V types).", nameof(kvTypeV));
        }

        if (kvKeySet && !flashAttn)
        {
            throw new ArgumentException("Flash attention must be enabled when KV cache types are set (quantized/explicit KV requires flash attention).",
                nameof(flashAttn));
        }

        return new ResolvedLaunchArguments
        {
            ExploreMode = false,
            CtxSize = ctxSize,
            NGpuLayers = nGpuLayers,
            TensorSplit = tensorSplit,
            OverrideTensor = overrideTensor,
            KvTypeK = kvTypeK,
            KvTypeV = kvTypeV,
            FlashAttn = flashAttn
        };
    }

    /// <summary>
    ///     This replay with its explicit KV cache types stripped (and flash attention with them, since the two are
    ///     coupled by the invariants above) — the safe retry candidate for a frozen profile whose quantized-KV config
    ///     cannot reach readiness on the current backend. Placement (<c>-c/-ngl/-ts/-ot</c>) is untouched.
    /// </summary>
    public ResolvedLaunchArguments WithoutKvCacheQuantization()
    {
        return this with
        {
            KvTypeK = null,
            KvTypeV = null,
            FlashAttn = false
        };
    }
}
