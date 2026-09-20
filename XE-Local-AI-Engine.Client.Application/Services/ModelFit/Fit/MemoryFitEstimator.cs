namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fit;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Pure, I/O-free estimator of whether a GGUF model fits the node's memory budget, implementing the oobabooga
///     "GGUF VRAM formula" (<see href="https://oobabooga.github.io/blog/posts/gguf-vram-formula/" />).
/// </summary>
/// <remarks>
///     <c>total ≈ weights(quant) + KV_cache + ~0.75 GB CUDA/runtime overhead + safety margin</c>, with
///     <c>KV_cache = n_layers · n_kv_heads · (key_dim + value_dim) · ctx · bytesPerKvElement(kvQuant)</c>. A model fits
///     iff <c>total ≤ budget</c>: GPU VRAM when acceleration is available and VRAM was measured, the node's available
///     RAM in CPU mode (the degrade rule). Stateless and Singleton-safe, parsing no GGUF — every header input comes
///     from the discovery per-file DTO. See docs/wiki/07-model-fit.md ("The memory-fit estimator (pure core)").
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
    ///     active-parameter count.
    /// </summary>
    /// <remarks>
    ///     Expert FFN tensors dominate the parameter count in typical llama.cpp MoE architectures
    ///     (Mixtral/Qwen-MoE/DeepSeek-MoE style), so 85% is deliberately conservative — it over-estimates the
    ///     CPU-offloaded share and under-estimates the GPU-resident share — and is used only when a more precise
    ///     <see cref="MoeFacts.ActiveParamCount" /> figure is unavailable.
    /// </remarks>
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
    ///     Estimates the memory footprint of a model with the given GGUF header metadata at
    ///     <paramref name="ctxTarget" /> tokens against <paramref name="profile" />.
    /// </summary>
    /// <param name="quant">The chosen quant label (e.g. <c>Q4_K_M</c>) — drives bytes-per-weight when a param count is present.</param>
    /// <param name="paramCount">GGUF param count (n_params), or <see langword="null" /> to fall back to <paramref name="fileSizeBytes" />.</param>
    /// <param name="fileSizeBytes">The on-disk quantized file size; the weights fallback when <paramref name="paramCount" /> is null.</param>
    /// <param name="embeddingLength">Embedding length; the derived <c>head_dim = embeddingLength / n_heads</c> fallback when <paramref name="attention" /> carries none.</param>
    /// <param name="attentionHeadCount">n_heads — the divisor for the derived head_dim fallback.</param>
    /// <param name="kvCacheQuantized">When <see langword="true" />, KV cache is 8-bit (1 byte/element) instead of fp16. Ignored when <paramref name="kvCacheQuant" /> is supplied.</param>
    /// <param name="moeFacts">MoE facts; when supplied and <see cref="MoeFacts.IsMoe" />, an over-budget resident estimate is retried as an expert-offload split.</param>
    /// <param name="kvCacheQuant">Explicit KV-cache quantization (F16/Q8_0/Q4_0); when non-null it overrides <paramref name="kvCacheQuantized" />.</param>
    /// <param name="attention">Explicit attention geometry (key/value lengths, sliding-window facts); null derives head_dim and treats every layer as full-attention.</param>
    /// <param name="nativeQuantFormat">Marks <paramref name="quant" /> a native, non-requantizable format (MXFP4), so the ladder walk never prefers a requant over it.</param>
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
            return new MemoryFitEstimate
            {
                Fits = true,
                EstimatedBytes = residentEstimatedBytes,
                HeadroomBytes = residentHeadroomBytes,
                Mode = mode,
                MoeVerdict = MoeFitVerdict.FitsResident,
                GpuBytes = null,
                CpuBytes = null,
                ExpertsOffloaded = false,
                Confidence = confidence,
                NativeQuantFormat = nativeQuantFormat
            };
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
                return new MemoryFitEstimate
                {
                    Fits = true,
                    EstimatedBytes = gpuBytes + cpuBytes,
                    HeadroomBytes = budgetBytes - gpuBytes,
                    Mode = mode,
                    MoeVerdict = MoeFitVerdict.FitsWithExpertOffload,
                    GpuBytes = gpuBytes,
                    CpuBytes = cpuBytes,
                    ExpertsOffloaded = true,
                    Confidence = confidence,
                    NativeQuantFormat = nativeQuantFormat
                };
            }
        }

        return new MemoryFitEstimate
        {
            Fits = false,
            EstimatedBytes = residentEstimatedBytes,
            HeadroomBytes = residentHeadroomBytes,
            Mode = mode,
            MoeVerdict = MoeFitVerdict.DoesNotFit,
            GpuBytes = null,
            CpuBytes = null,
            ExpertsOffloaded = false,
            Confidence = confidence,
            NativeQuantFormat = nativeQuantFormat
        };
    }

    /// <summary>
    ///     The memory budget an estimate for <paramref name="profile" /> is scored against: the GPU budget in GPU mode
    ///     (free VRAM when the probe supplied it, total dedicated VRAM otherwise) and the node's available RAM under
    ///     the CPU degrade rule.
    /// </summary>
    /// <remarks>
    ///     Exposed so a caller that presents or normalizes a fit figure uses the IDENTICAL number this estimator
    ///     scored against. Re-deriving the expression inline is how the advisor's score came to disagree with its own
    ///     fit verdicts: one of two inline copies was missed when the GPU budget moved from total to free VRAM. There
    ///     is one definition and no way to drift.
    /// </remarks>
    public static long ResolveFitBudgetBytes(HardwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return UsesGpuBudget(profile) ? ResolveGpuBudgetBytes(profile) : profile.AvailableRamBytes;
    }

    /// <summary>
    ///     Given the fitting candidates for ONE model repo, drops any candidate that is a pointless REQUANT of a model
    ///     that also ships a native, non-requantizable format (MXFP4 / NVFP4).
    /// </summary>
    /// <remarks>
    ///     The weights are already at their trained precision, so re-encoding them at a higher nominal quality, or at
    ///     the same 4-bit width in a lossy K-quant, buys nothing and costs disk and memory. The best native file caps
    ///     the repo on BOTH axes: a non-native candidate is dropped when it ranks strictly higher quality than that
    ///     native file AND is no denser-packed (<see cref="BytesPerWeight" /> at or above the native's). With no
    ///     native candidate the list is returned unchanged; a lower <paramref name="rankOf" /> is a higher quality.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    ///     Unknown labels fall back to the Q4_K_M density (~0.5625 bytes/weight ≈ 4.5 bits) — a conservative middle
    ///     ground. The I-quant (IQ*) bit-widths are the MEASURED effective bpw from the llama.cpp Llama-3.1-8B
    ///     quantize benchmark, so an IQ file is sized at its true density instead of the legacy 4.5 bpw default.
    ///     <c>MXFP4</c> is gpt-oss's native ~4.25 bits/weight MoE format.
    /// </remarks>
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
            // NVFP4 is priced at MXFP4's density by MEASUREMENT, not theory: s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF ships SAME-model, SAME-converter MXFP4 and NVFP4 conversions at 5.45 GB each.
            // Cross-repo NVFP4 sizes for one base model vary widely (Qwen3.6-27B: 16.19 vs 19.88 GB) because converters differ in what they keep at high precision, so only a same-repo pair is sound.
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

    /// <summary>The GPU-mode fit budget: free VRAM when it was measured, otherwise total dedicated VRAM.</summary>
    /// <remarks>
    ///     Free VRAM is the direct analogue of CPU mode's <see cref="HardwareProfile.AvailableRamBytes" /> and what
    ///     the launcher has to place layers into — a compositor, browser and any warm sub-agent server routinely hold
    ///     1.5–2.5 GB of a 16 GB card. Budgeting against TOTAL VRAM scores models as fitting that then demand-page to
    ///     host RAM on WDDM: no error, no OOM, just a multiple-times slowdown reading as a broken app. Falls back to
    ///     total when free VRAM is unavailable (only NVIDIA reports it) or non-positive, never to zero.
    /// </remarks>
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
    ///     would be offloaded to system RAM under <c>--n-cpu-moe</c>.
    /// </summary>
    /// <remarks>
    ///     Prefers <see cref="MoeFacts.ActiveParamCount" /> when it and <paramref name="totalParamCount" /> are both
    ///     known: <c>expertParams ≈ totalParams − activeParams</c>, a conservative approximation because "active"
    ///     params include the currently-routed experts' share, so it slightly over-counts the expert-only portion and
    ///     biases the split toward the more spacious CPU/RAM side. Falls back to
    ///     <see cref="DefaultExpertWeightShareFraction" />. Assumes uniform quant density across all tensors.
    /// </remarks>
    private static long EstimateExpertWeightsBytes(long weightsBytes, long? totalParamCount, MoeFacts moeFacts)
    {
        if (moeFacts.ActiveParamCount is { } active && totalParamCount is { } total && total > active && active > 0)
        {
            var expertParamFraction = (total - active) / (double)total;
            return (long)(weightsBytes * expertParamFraction);
        }

        return (long)(weightsBytes * DefaultExpertWeightShareFraction);
    }

    // The (bool, nullable-enum) pair collapsed to the one enum the KV formula actually needs, byte-identical to the pair it replaces:
    // an explicit quant wins, and an absent one is Q8_0 when the caller asked for a quantized KV cache and F16 otherwise — the same 1 vs 2 bytes/element.
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

    // KV-cache bytes for all layers, from the GGUF's explicit per-head key/value dimensions when supplied (Qwen3-style decoupled head_dim), else head_dim = embedding_length / n_heads.
    // Interleaved sliding-window layers are capped at the window rather than the full context. Also reports whether head_dim was derived, for the estimate's confidence.
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

        // Multi-head Latent Attention (deepseek2). The MLA term is CLAMPED with Math.Max against the generic term: it can only ever RAISE the estimate, never lower it, because two
        // inputs to the MLA row width are ASSUMPTIONS, not facts. Why the clamp and what evidence would remove it: docs/wiki/07-model-fit.md ("The memory-fit estimator (pure core)").
        if (attention?.IsMla == true)
        {
            var mlaPerLayerPerToken = attention.KeyLengthMla!.Value * bytesPerElement;
            perLayerPerToken = Math.Max(mlaPerLayerPerToken, perLayerPerToken);
        }

        var totalTokensAcrossLayers = TotalKvTokensAcrossLayers(blockCount, ctxTarget, attention);
        return new KvCacheEstimate((long)(perLayerPerToken * totalTokensAcrossLayers), headDimDerived);
    }

    // The summed per-layer context lengths the KV cache must hold. Dense or global attention holds the full context on every layer. Interleaved sliding-window attention (Gemma)
    // holds it only on the global layers — every pattern-th — and caps each window-limited local layer at min(context, window), matching llama.cpp's separate smaller cache.
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
