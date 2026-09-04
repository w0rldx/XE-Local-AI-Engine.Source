namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fit;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Pure, I/O-free estimator of whether a GGUF model fits the node's memory budget. Implements the oobabooga
///     "GGUF VRAM formula" (<see href="https://oobabooga.github.io/blog/posts/gguf-vram-formula/" />):
///     <code>
///     total ≈ weights(quant) + KV_cache + ~0.75 GB CUDA/runtime overhead + safety margin
///     KV_cache = n_layers · n_kv_heads · (key_dim + value_dim) · ctx · bytesPerKvElement(kvQuant)
///     </code>
///     with two 2026-era corrections: the per-head key/value dimensions come from the GGUF's explicit
///     <c>{arch}.attention.key_length</c>/<c>value_length</c> when present (the derived <c>embedding_length / n_heads</c>
///     is wrong for families like Qwen3 that pin <c>head_dim = 128</c>), and interleaved sliding-window attention (Gemma
///     family) caps the window-limited layers' KV at the window instead of the full context. The budget is the GPU VRAM
///     when GPU acceleration is available and VRAM was measured (<see cref="HardwareProfile.GpuAccelAvailable" /> &amp;&amp;
///     <see cref="HardwareProfile.VramKnown" />) — free VRAM (<see cref="HardwareProfile.AvailableVramBytes" />) when the
///     probe supplied it, total dedicated VRAM otherwise; and the node's available RAM in CPU mode (the degrade rule). It
///     performs no GGUF parsing — every header input is supplied by the Hugging Face GGUF discovery per-file DTO. A model
///     fits iff <c>total ≤ budget</c>.
/// </summary>
/// <remarks>
///     Singleton-safe (stateless). The <c>weights</c> term prefers the header param count × bytes-per-weight of the
///     chosen quant; when the param count is unavailable it falls back to the file's on-disk byte size (the quantized
///     weights already on disk), so a file is never rejected purely for a missing param count when its size is known.
///     Estimates whose head_dim was derived (no explicit key/value length) or whose weights fell back to the file size are
///     flagged <see cref="FitConfidence.Approximate" /> so the advisor can present a conservative figure.
/// </remarks>
public sealed class MemoryFitEstimator
{
    /// <summary>Fixed CUDA/runtime overhead added to every estimate (~0.75 GB, oobabooga formula).</summary>
    public const long RuntimeOverheadBytes = 768L * 1024 * 1024;

    /// <summary>The default quant the advisor selects when the operator supplies no override (HF default policy).</summary>
    public const string DefaultQuant = "Q4_K_M";

    /// <summary>
    ///     Default fractional safety margin applied to <c>weights + KV</c> before the fixed overhead, to absorb the
    ///     formula's under-estimation (fragmentation, activation buffers). 12% is the conservative default.
    /// </summary>
    public const double DefaultSafetyMarginFraction = 0.12d;

    /// <summary>
    ///     Conservative default fraction of total weight bytes assumed to live in expert (MoE FFN) tensors when the
    ///     caller supplies <see cref="MoeFacts.ExpertCount" />/<see cref="MoeFacts.ExpertUsedCount" /> but no published
    ///     active-parameter count. Expert FFN tensors dominate the parameter count in typical llama.cpp MoE
    ///     architectures (Mixtral/Qwen-MoE/DeepSeek-MoE style), so 85% is a deliberately conservative (i.e. it
    ///     over-estimates the CPU-offloaded share and under-estimates the GPU-resident share) placeholder used only when
    ///     a more precise <see cref="MoeFacts.ActiveParamCount" /> figure is unavailable.
    /// </summary>
    public const double DefaultExpertWeightShareFraction = 0.85d;

    private readonly double _safetyMarginFraction;

    /// <summary>Creates an estimator with the default ~0.75 GB overhead and 12% safety margin.</summary>
    public MemoryFitEstimator()
        : this(RuntimeOverheadBytes, DefaultSafetyMarginFraction)
    {
    }

    /// <summary>Creates an estimator with an explicit runtime overhead and safety margin (test/tuning hook).</summary>
    public MemoryFitEstimator(long overheadBytes, double safetyMarginFraction)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(overheadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(safetyMarginFraction);
        OverheadBytes = overheadBytes;
        _safetyMarginFraction = safetyMarginFraction;
    }

    /// <summary>The fixed runtime overhead this estimator adds to every estimate (the insufficient-metadata floor).</summary>
    public long OverheadBytes { get; }

    /// <summary>
    ///     Estimates the memory footprint of a model with the given GGUF header metadata at <paramref name="ctxTarget" />
    ///     tokens against <paramref name="profile" />. <paramref name="kvCacheQuantized" /> selects an 8-bit KV cache
    ///     (1 byte/element) instead of the default fp16 (2 bytes/element), lowering the KV term so a larger model can fit;
    ///     pass <paramref name="kvCacheQuant" /> instead for a 3-way choice (F16/Q8_0/Q4_0) — when non-null it overrides
    ///     <paramref name="kvCacheQuantized" />.
    /// </summary>
    /// <param name="quant">The chosen quant label (e.g. <c>Q4_K_M</c>) — drives bytes-per-weight when a param count is present.</param>
    /// <param name="paramCount">GGUF param count (n_params), or <see langword="null" /> to fall back to <paramref name="fileSizeBytes" />.</param>
    /// <param name="fileSizeBytes">The on-disk quantized file size; the weights fallback when <paramref name="paramCount" /> is null.</param>
    /// <param name="blockCount">n_layers (<c>BlockCount</c>).</param>
    /// <param name="attentionHeadCountKV">n_kv_heads (<c>AttentionHeadCountKV</c>).</param>
    /// <param name="embeddingLength">Embedding length; the derived <c>head_dim = embeddingLength / n_heads</c> fallback when <paramref name="attention" /> carries no explicit key/value length.</param>
    /// <param name="attentionHeadCount">n_heads (<c>AttentionHeadCount</c>) — the divisor for the derived head_dim fallback.</param>
    /// <param name="ctxTarget">Target context window in tokens for the KV-cache sizing.</param>
    /// <param name="profile">The hardware profile supplying the fit budget.</param>
    /// <param name="kvCacheQuantized">When <see langword="true" />, KV cache is 8-bit (1 byte/element) instead of fp16. Ignored when <paramref name="kvCacheQuant" /> is supplied.</param>
    /// <param name="moeFacts">
    ///     Optional Mixture-of-Experts facts. When <see langword="null" /> (the default) behavior is unchanged from the
    ///     dense-model estimate. When supplied and <see cref="MoeFacts.IsMoe" />, a resident estimate that exceeds the
    ///     budget is retried as an expert-offload split (GPU: non-expert weights + KV + overhead; CPU: expert weights) —
    ///     see <see cref="MoeFitVerdict.FitsWithExpertOffload" />.
    /// </param>
    /// <param name="kvCacheQuant">
    ///     Optional explicit KV-cache quantization (<see cref="KvCacheQuant.F16" />/<see cref="KvCacheQuant.Q8_0" />/
    ///     <see cref="KvCacheQuant.Q4_0" />). When <see langword="null" /> (the default) the legacy
    ///     <paramref name="kvCacheQuantized" /> bool decides between F16 and Q8_0 — fully behavior-preserving for
    ///     existing callers.
    /// </param>
    /// <param name="attention">
    ///     Optional explicit attention geometry (key/value lengths + sliding-window facts). When <see langword="null" />
    ///     (the default) the estimator derives <c>head_dim = embedding_length / n_heads</c> and treats every layer as
    ///     full-attention — the exact legacy behavior. Supplying it corrects the KV term for families with a decoupled
    ///     head_dim (Qwen3) or interleaved sliding-window attention (Gemma).
    /// </param>
    /// <param name="nativeQuantFormat">
    ///     When <see langword="true" />, <paramref name="quant" /> is a native, non-requantizable format (MXFP4). Surfaced
    ///     on the estimate so the advisor's ladder walk never prefers a higher-nominal-quality requant over it.
    /// </param>
    public MemoryFitEstimate Estimate(string quant,
        long? paramCount,
        long fileSizeBytes,
        long blockCount,
        long attentionHeadCountKV,
        long embeddingLength,
        long attentionHeadCount,
        long ctxTarget,
        HardwareProfile profile,
        bool kvCacheQuantized,
        MoeFacts? moeFacts = null,
        KvCacheQuant? kvCacheQuant = null,
        GgufAttentionShape? attention = null,
        bool nativeQuantFormat = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        ArgumentNullException.ThrowIfNull(profile);

        var weightsBytes = EstimateWeightsBytes(quant, paramCount, fileSizeBytes);
        var kv = EstimateKvCacheFootprint(blockCount,
            attentionHeadCountKV,
            embeddingLength,
            attentionHeadCount,
            ctxTarget,
            ResolveKvCacheQuant(kvCacheQuantized, kvCacheQuant),
            attention);
        var kvBytes = kv.BytesAtContext;

        // The estimate is approximate whenever a required input was derived or fell back: weights from the on-disk file
        // size (no param count), or head_dim from embedding_length / n_heads (no explicit key/value length in the header).
        var approximate = paramCount is not > 0 || kv.HeadDimDerived;
        var confidence = approximate ? FitConfidence.Approximate : FitConfidence.Exact;

        var useGpu = UsesGpuBudget(profile);
        var budgetBytes = ResolveFitBudgetBytes(profile);
        var mode = useGpu ? FitMode.Gpu : FitMode.Cpu;

        // Apply the safety margin to the model-driven terms (weights + KV) only, then add the fixed runtime overhead.
        var marginBytes = (long)((weightsBytes + kvBytes) * _safetyMarginFraction);
        var residentEstimatedBytes = weightsBytes + kvBytes + marginBytes + OverheadBytes;
        var residentHeadroomBytes = budgetBytes - residentEstimatedBytes;

        if (residentEstimatedBytes <= budgetBytes)
        {
            return new MemoryFitEstimate(Fits: true, residentEstimatedBytes, residentHeadroomBytes, mode, MoeFitVerdict.FitsResident,
                GpuBytes: null, CpuBytes: null, ExpertsOffloaded: false, confidence, nativeQuantFormat);
        }

        // Resident estimate exceeds the budget — only MoE models on a GPU node can retry via expert offload
        // (llama.cpp --n-cpu-moe keeps attention/shared/router tensors on GPU and moves expert tensors to system RAM).
        if (moeFacts is { IsMoe: true } && useGpu)
        {
            var expertWeightsBytes = EstimateExpertWeightsBytes(weightsBytes, paramCount, moeFacts);
            var nonExpertWeightsBytes = Math.Max(val1: 0L, weightsBytes - expertWeightsBytes);
            var gpuMarginBytes = (long)((nonExpertWeightsBytes + kvBytes) * _safetyMarginFraction);
            var gpuBytes = nonExpertWeightsBytes + kvBytes + gpuMarginBytes + OverheadBytes;
            var cpuBytes = expertWeightsBytes;

            if (gpuBytes <= budgetBytes && cpuBytes <= profile.AvailableRamBytes)
            {
                return new MemoryFitEstimate(Fits: true,
                    EstimatedBytes: gpuBytes + cpuBytes,
                    HeadroomBytes: budgetBytes - gpuBytes,
                    mode,
                    MoeFitVerdict.FitsWithExpertOffload,
                    gpuBytes,
                    cpuBytes,
                    ExpertsOffloaded: true,
                    confidence,
                    nativeQuantFormat);
            }
        }

        return new MemoryFitEstimate(Fits: false, residentEstimatedBytes, residentHeadroomBytes, mode, MoeFitVerdict.DoesNotFit,
            GpuBytes: null, CpuBytes: null, ExpertsOffloaded: false, confidence, nativeQuantFormat);
    }

    /// <summary>
    ///     The memory budget an estimate for <paramref name="profile" /> is scored against: the GPU budget in GPU mode
    ///     (free VRAM when the probe supplied it, total dedicated VRAM otherwise) and the node's available RAM under the
    ///     CPU degrade rule. Exposed so a caller that presents or normalizes a fit figure uses the IDENTICAL number this
    ///     estimator scored against. Two callers previously re-derived this expression inline and one of them was missed
    ///     when the GPU budget moved from total to free VRAM, so the advisor's score silently disagreed with its own fit
    ///     verdicts; there is now one definition and no way to drift.
    /// </summary>
    public static long ResolveFitBudgetBytes(HardwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return UsesGpuBudget(profile) ? ResolveGpuBudgetBytes(profile) : profile.AvailableRamBytes;
    }

    /// <summary>
    ///     Given the fitting candidates for ONE model repo, drops any candidate that is a pointless REQUANT of a model
    ///     that also ships a native, non-requantizable format (MXFP4 / NVFP4). The weights are already at their trained
    ///     precision, so re-encoding them at a higher nominal quality, or at the same 4-bit width in a lossy K-quant,
    ///     buys nothing and costs disk and memory. The best native file therefore caps the repo on BOTH axes: a
    ///     non-native candidate is dropped when it ranks strictly higher quality than that native file, and also when it
    ///     is no denser-packed than it (<see cref="BytesPerWeight" /> at or above the native's). What survives is every
    ///     native file plus the genuinely smaller lower-quality quants a tight box still needs (Q3_K_M, Q2_K, …). When no
    ///     native-format candidate is present the list is returned unchanged. Pure and generic so both advisor lanes
    ///     share one guard (lower <paramref name="rankOf" /> == higher quality, per <see cref="QuantLadder.QualityRank" />).
    /// </summary>
    public static IReadOnlyList<T> FilterOutNativeFormatRequants<T>(IReadOnlyList<T> candidates,
        Func<T, string> quantOf,
        Func<T, int> rankOf)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(quantOf);
        ArgumentNullException.ThrowIfNull(rankOf);

        var natives = candidates
                      .Where(candidate => QuantLadder.IsNativeFormat(quantOf(candidate)))
                      .ToList();

        if (natives.Count == 0)
        {
            return candidates; // no native-format file in the repo — nothing to guard.
        }

        var rankThreshold = natives.Min(rankOf);
        var densityThreshold = natives.Min(candidate => DensityOf(quantOf(candidate)));
        return candidates
               .Where(candidate => QuantLadder.IsNativeFormat(quantOf(candidate))
                                   || (rankOf(candidate) >= rankThreshold && DensityOf(quantOf(candidate)) < densityThreshold))
               .ToList();
    }

    /// <summary>
    ///     Bytes-per-weight for a quant label (the dominant llama.cpp K-quants, I-quants, native and legacy/full types).
    ///     Unknown labels fall back to the Q4_K_M density (~0.5625 bytes/weight ≈ 4.5 bits) — a conservative middle ground.
    ///     The I-quant (IQ*) bit-widths are the measured effective bpw from the llama.cpp Llama-3.1-8B quantize benchmark, so
    ///     an IQ file is sized at its true density instead of the legacy 4.5bpw default; <c>MXFP4</c> is gpt-oss's native
    ///     ~4.25 bits/weight MoE format.
    /// </summary>
    public static double BytesPerWeight(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);

        // Approximate effective bits-per-weight → bytes-per-weight. Sourced from llama.cpp quant type bit-widths.
        return quant.Trim().ToUpperInvariant() switch
        {
            "IQ1_S" => 2.0042d / 8d,
            "IQ1_M" => 2.146d / 8d,
            "IQ2_XXS" => 2.3824d / 8d,
            "IQ2_XS" => 2.5882d / 8d,
            "Q2_K" => 2.625d / 8d,
            "IQ2_S" => 2.7403d / 8d,
            "IQ2_M" => 2.9294d / 8d,
            "IQ3_XXS" => 3.2548d / 8d,
            "Q3_K_S" or "Q3_K_M" or "Q3_K_L" or "Q3_K" => 3.4375d / 8d,
            "IQ3_XS" => 3.4977d / 8d,
            "IQ3_S" => 3.6606d / 8d,
            "IQ3_M" => 3.7628d / 8d,
            // NVFP4 is priced at MXFP4's density from measurement, not theory: s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF
            // ships MXFP4 and NVFP4 conversions of the SAME model from the SAME converter at byte-identical sizes
            // (5.45 GB each, sampled 2026-07-31). Cross-repo NVFP4 sizes for one base model vary widely
            // (Qwen3.6-27B: 16.19 GB vs 19.88 GB) because converters differ in how much they keep at high precision,
            // so a same-repo pair is the only sound apples-to-apples signal.
            "MXFP4" or "NVFP4" => 4.25d / 8d,
            "Q4_0" or "Q4_1" => 4.5d / 8d,
            "Q4_K_S" or "Q4_K_M" or "Q4_K" => 4.5d / 8d,
            "IQ4_XS" => 4.4597d / 8d,
            "IQ4_NL" => 4.6818d / 8d,
            "Q5_0" or "Q5_1" => 5.5d / 8d,
            "Q5_K_S" or "Q5_K_M" or "Q5_K" => 5.5d / 8d,
            "Q6_K" => 6.5625d / 8d,
            "Q8_0" => 8.5d / 8d,
            "F16" or "FP16" or "BF16" => 16d / 8d,
            "F32" or "FP32" => 32d / 8d,
            _ => 4.5d / 8d
        };
    }

    // Whether an estimate for this profile is scored against the GPU budget rather than the CPU/RAM degrade budget.
    private static bool UsesGpuBudget(HardwareProfile profile)
    {
        return profile is { GpuAccelAvailable: true, VramKnown: true } && profile.VramBytes is > 0;
    }

    /// <summary>
    ///     The GPU-mode fit budget: free VRAM when it was measured, otherwise total dedicated VRAM. Free VRAM is the
    ///     direct analogue of the CPU mode's <see cref="HardwareProfile.AvailableRamBytes" /> and is what the launcher
    ///     actually has to place layers into — a desktop compositor, browser, and any warm sub-agent server routinely
    ///     hold 1.5–2.5 GB of a 16 GB card before the first model loads. Budgeting against TOTAL VRAM instead scored
    ///     models as fitting that then demand-page to host RAM on WDDM: no error, no OOM, just a multiple-times
    ///     slowdown, which reads as "the app is broken" rather than "the model is too big". Falls back to total when
    ///     free VRAM is unavailable (only NVIDIA reports it) or reads as non-positive, so a missing or nonsensical
    ///     probe never collapses the budget to zero and drops every model.
    /// </summary>
    private static long ResolveGpuBudgetBytes(HardwareProfile profile)
    {
        return profile.AvailableVramBytes is > 0 ? profile.AvailableVramBytes.Value : profile.VramBytes!.Value;
    }

    // Bytes-per-weight of a possibly Unsloth-Dynamic-prefixed label, for the native-format guard's width comparison.
    private static double DensityOf(string quant)
    {
        return BytesPerWeight(GgufQuantParser.StripDynamicPrefix(quant.Trim()));
    }

    private static long EstimateWeightsBytes(string quant, long? paramCount, long fileSizeBytes)
    {
        if (paramCount is { } parameters && parameters > 0)
        {
            return (long)(parameters * BytesPerWeight(quant));
        }

        // No param count → the already-quantized file size is the best available weights estimate (clamped non-negative).
        return fileSizeBytes > 0 ? fileSizeBytes : 0;
    }

    /// <summary>
    ///     Approximates the byte share of <paramref name="weightsBytes" /> that lives in expert (MoE FFN) tensors and
    ///     would be offloaded to system RAM under <c>--n-cpu-moe</c>. Prefers <see cref="MoeFacts.ActiveParamCount" />
    ///     when both it and <paramref name="totalParamCount" /> are known: <c>expertParams ≈ totalParams − activeParams</c>
    ///     (a conservative approximation — "active" params include the currently-routed experts' share, so this slightly
    ///     over-counts the expert-only portion, which biases the split toward the more spacious CPU/RAM side rather than
    ///     GPU/VRAM). Falls back to <see cref="DefaultExpertWeightShareFraction" /> of <paramref name="weightsBytes" />
    ///     when no active-param figure is available (only <see cref="MoeFacts.ExpertCount" />/
    ///     <see cref="MoeFacts.ExpertUsedCount" /> known). Assumes uniform quant density across expert and non-expert
    ///     tensors.
    /// </summary>
    private static long EstimateExpertWeightsBytes(long weightsBytes, long? totalParamCount, MoeFacts moeFacts)
    {
        if (moeFacts.ActiveParamCount is { } active && totalParamCount is { } total && total > active && active > 0)
        {
            var expertParamFraction = (total - active) / (double)total;
            return (long)(weightsBytes * expertParamFraction);
        }

        return (long)(weightsBytes * DefaultExpertWeightShareFraction);
    }

    // The legacy (bool, nullable-enum) pair collapsed to the one enum the KV formula actually needs. Byte-identical to
    // the pair it replaces: an explicit quant wins, and an absent one is Q8_0 when the caller asked for a quantized KV
    // cache and F16 otherwise — the same 1 vs 2 bytes/element the pair produced.
    private static KvCacheQuant ResolveKvCacheQuant(bool kvCacheQuantized, KvCacheQuant? kvCacheQuant)
    {
        return kvCacheQuant ?? (kvCacheQuantized ? KvCacheQuant.Q8_0 : KvCacheQuant.F16);
    }

    private static double ResolveKvBytesPerElement(KvCacheQuant kvCacheQuant)
    {
        return kvCacheQuant switch
        {
            KvCacheQuant.Q8_0 => 1d,
            KvCacheQuant.Q4_0 => 0.5d,
            _ => 2d
        };
    }

    /// <summary>
    ///     The KV cache this geometry needs at <paramref name="ctxTarget" />, at an EXPLICITLY named element size. The
    ///     one KV formula in the application: <see cref="Estimate" /> calls this too, so a figure shown to an operator
    ///     and the figure the admission ledger reserves can never drift apart.
    /// </summary>
    /// <remarks>
    ///     <paramref name="kvCacheQuant" /> is required and is echoed on the result because a bare "KV bytes/token" is
    ///     ambiguous by a factor of two: a candidate's ranking estimate is fp16-sized by contract while the chat launch
    ///     runs <c>q8_0</c>. Every consumer must label the number with the quant it came back with.
    /// </remarks>
    public static KvCacheFootprint EstimateKvCacheFootprint(long blockCount,
        long attentionHeadCountKV,
        long embeddingLength,
        long attentionHeadCount,
        long ctxTarget,
        KvCacheQuant kvCacheQuant,
        GgufAttentionShape? attention = null)
    {
        var estimate = EstimateKvCacheBytes(blockCount,
            attentionHeadCountKV,
            embeddingLength,
            attentionHeadCount,
            ctxTarget,
            ResolveKvBytesPerElement(kvCacheQuant),
            attention);
        // Bytes/token is the total divided by the requested context, so an interleaved sliding-window model reports the
        // AVERAGE per-token cost across its layers rather than a full-attention figure it never pays.
        var bytesPerToken = ctxTarget > 0 ? estimate.Bytes / (double)ctxTarget : 0d;
        return new KvCacheFootprint(estimate.Bytes, bytesPerToken, kvCacheQuant, estimate.HeadDimDerived);
    }

    // KV-cache bytes across all layers. Uses the GGUF's explicit per-head key/value dimensions when supplied (correct for
    // Qwen3-style decoupled head_dim) and derives head_dim = embedding_length / n_heads otherwise; interleaved
    // sliding-window layers are capped at the window rather than the full context. Also reports whether head_dim was
    // derived (for the estimate's confidence).
    private static KvCacheEstimate EstimateKvCacheBytes(long blockCount,
        long attentionHeadCountKV,
        long embeddingLength,
        long attentionHeadCount,
        long ctxTarget,
        double bytesPerElement,
        GgufAttentionShape? attention)
    {
        if (blockCount <= 0 || attentionHeadCountKV <= 0 || ctxTarget <= 0)
        {
            return new KvCacheEstimate(Bytes: 0, HeadDimDerived: false);
        }

        var explicitKey = attention?.KeyLength is > 0 ? attention.KeyLength : null;
        var explicitValue = attention?.ValueLength is > 0 ? attention.ValueLength : null;

        double keyDim;
        double valueDim;
        bool headDimDerived;
        if (explicitKey is { } k && explicitValue is { } v)
        {
            // Explicit {arch}.attention.key_length / value_length — exact, and required for families (Qwen3) whose
            // head_dim is decoupled from embedding_length / n_heads.
            keyDim = k;
            valueDim = v;
            headDimDerived = false;
        }
        else if (embeddingLength > 0 && attentionHeadCount > 0)
        {
            // Legacy fallback: derive a symmetric head_dim from the embedding width and head count (an explicit key or
            // value length, if only one is present, still overrides its side).
            var derived = embeddingLength / (double)attentionHeadCount;
            keyDim = explicitKey ?? derived;
            valueDim = explicitValue ?? derived;
            headDimDerived = explicitKey is null && explicitValue is null;
        }
        else
        {
            // Neither explicit lengths nor a derivable head_dim → cannot size the KV cache term.
            return new KvCacheEstimate(Bytes: 0, HeadDimDerived: false);
        }

        // Per-layer, per-token KV bytes: n_kv_heads · (key_dim + value_dim) · bytes/element. Equals the legacy
        // 2 · n_kv_heads · head_dim · bytes when key_dim == value_dim == head_dim (symmetric derived head_dim).
        var perLayerPerToken = attentionHeadCountKV * (keyDim + valueDim) * bytesPerElement;

        // Multi-head Latent Attention (deepseek2): llama.cpp allocates ONE latent K tensor per layer and no V tensor at
        // all, so the row is key_length_mla wide with a single KV head — far below the generic figure above. Two inputs
        // to that width are ASSUMPTIONS, not facts: llama.cpp sizes the cache as n_embd_head_k · n_head_kv (not
        // n_embd_head_k_mla), so both "the row is key_length_mla wide" and "n_head_kv is 1 under MLA" depend on what
        // the deepseek2 loader writes into those hparams, and neither is provable from the published sources.
        // This estimate is not display-only — ProcessContextAllocationResolver turns it into the ResourceFootprint the
        // VRAM admission ledger reserves, and an under-estimate admits a model that then OOMs on load. So the MLA term
        // is CLAMPED with Math.Max against the generic term: it can only ever raise the estimate, never lower it, until
        // a live measurement against llama.cpp's own /metrics KV figure says which assumption holds. Removing the clamp
        // is a one-line change gated on that evidence.
        // The clamp's generic term is whatever the ladder above produced. When neither rung is computable this method
        // has already returned the zero estimate, so a header carrying key_length_mla but no usable key/value geometry
        // never clamps against zero and never ships the bare MLA figure.
        if (attention?.IsMla == true)
        {
            var mlaPerLayerPerToken = attention.KeyLengthMla!.Value * bytesPerElement;
            perLayerPerToken = Math.Max(mlaPerLayerPerToken, perLayerPerToken);
        }

        var totalTokensAcrossLayers = TotalKvTokensAcrossLayers(blockCount, ctxTarget, attention);
        return new KvCacheEstimate((long)(perLayerPerToken * totalTokensAcrossLayers), headDimDerived);
    }

    // The summed per-layer context lengths the KV cache must hold. Dense or global attention holds the full context on
    // every layer. Interleaved sliding-window attention (the Gemma family) holds the full context only on the global
    // layers — every pattern-th layer — and caps each remaining window-limited local layer at the smaller of the context
    // and the window, matching llama.cpp's separate, smaller sliding-window cache.
    private static double TotalKvTokensAcrossLayers(long blockCount, long ctxTarget, GgufAttentionShape? attention)
    {
        var window = attention?.SlidingWindow is > 0 ? attention.SlidingWindow : null;
        var pattern = attention?.SlidingWindowPattern is > 0 ? attention.SlidingWindowPattern : null;

        if (window is { } w && w < ctxTarget && pattern is { } p && p >= 1)
        {
            // ceil(blockCount / pattern) global layers — round UP so the full-context layers are never under-counted.
            var globalLayers = (blockCount + p - 1) / p;
            var swaLayers = blockCount - globalLayers;
            return (globalLayers * (double)ctxTarget) + (swaLayers * (double)w);
        }

        // No interleaved SWA (or window ≥ ctx, so it never binds): every layer holds a full-context KV cache.
        return blockCount * (double)ctxTarget;
    }

    // KV-cache byte estimate plus whether head_dim was derived from embedding/heads (no explicit key/value length),
    // which downgrades the estimate's confidence to Approximate.
    private readonly record struct KvCacheEstimate(long Bytes, bool HeadDimDerived);
}
