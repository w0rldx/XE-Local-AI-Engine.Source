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
///     <see cref="HardwareProfile.VramKnown" />); otherwise the node's available RAM (the CPU-mode degrade rule). It
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
        var bytesPerKvElement = ResolveKvBytesPerElement(kvCacheQuantized, kvCacheQuant);
        var kv = EstimateKvCacheBytes(blockCount, attentionHeadCountKV, embeddingLength, attentionHeadCount, ctxTarget, bytesPerKvElement, attention);
        var kvBytes = kv.Bytes;

        // The estimate is approximate whenever a required input was derived or fell back: weights from the on-disk file
        // size (no param count), or head_dim from embedding_length / n_heads (no explicit key/value length in the header).
        var approximate = paramCount is not > 0 || kv.HeadDimDerived;
        var confidence = approximate ? FitConfidence.Approximate : FitConfidence.Exact;

        var useGpu = profile is { GpuAccelAvailable: true, VramKnown: true } && profile.VramBytes is > 0;
        var budgetBytes = useGpu ? profile.VramBytes!.Value : profile.AvailableRamBytes;
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
    ///     Given the fitting candidates for ONE model repo, drops any candidate that is a higher-nominal-quality REQUANT
    ///     of a model that also ships a native, non-requantizable format (MXFP4 — gpt-oss). Re-quantizing native 4-bit
    ///     weights up to Q6/Q8 only wastes space without adding quality, so the native file caps the repo's recommendable
    ///     quality: any non-native candidate ranked strictly HIGHER quality than the best native one is dropped. When no
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

        var nativeRanks = candidates
                          .Where(candidate => QuantLadder.IsNativeFormat(quantOf(candidate)))
                          .Select(rankOf)
                          .ToList();

        if (nativeRanks.Count == 0)
        {
            return candidates; // no native-format file in the repo — nothing to guard.
        }

        // The best (lowest-rank == highest-quality) native file caps the recommendable quality. Keep every native-format
        // file plus every non-native file that is NOT a higher-quality requant (rank at or below the native's quality).
        var threshold = nativeRanks.Min();
        return candidates
               .Where(candidate => QuantLadder.IsNativeFormat(quantOf(candidate)) || rankOf(candidate) >= threshold)
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
            "MXFP4" => 4.25d / 8d,
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

    private static double ResolveKvBytesPerElement(bool kvCacheQuantized, KvCacheQuant? kvCacheQuant)
    {
        return kvCacheQuant switch
        {
            KvCacheQuant.F16 => 2d,
            KvCacheQuant.Q8_0 => 1d,
            KvCacheQuant.Q4_0 => 0.5d,
            null => kvCacheQuantized ? 1d : 2d,
            _ => 2d
        };
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

/// <summary>Which memory budget an estimate was scored against.</summary>
public enum FitMode
{
    /// <summary>Scored against GPU VRAM (acceleration available and VRAM measured).</summary>
    Gpu = 0,

    /// <summary>Scored against available system RAM (no GPU acceleration or VRAM unknown — degrade rule).</summary>
    Cpu = 1
}

/// <summary>KV-cache element quantization for the KV-cache sizing term.</summary>
public enum KvCacheQuant
{
    /// <summary>fp16 KV cache — 2 bytes/element (today's default).</summary>
    F16 = 0,

    /// <summary>8-bit KV cache — 1 byte/element.</summary>
    Q8_0 = 1,

    /// <summary>4-bit KV cache — 0.5 bytes/element.</summary>
    Q4_0 = 2
}

/// <summary>
///     How confident the estimate is in its inputs. <see cref="Exact" /> means the weights and KV geometry came from
///     explicit GGUF metadata (param count + explicit attention key/value lengths). <see cref="Approximate" /> means at
///     least one input was derived or fell back (weights from the on-disk file size, or head_dim from
///     <c>embedding_length / n_heads</c>), so the advisor should present the figure conservatively.
/// </summary>
public enum FitConfidence
{
    /// <summary>Every required input was explicit — the estimate is precise.</summary>
    Exact = 0,

    /// <summary>A required input was derived or fell back — treat the estimate as a conservative approximation.</summary>
    Approximate = 1
}

/// <summary>
///     Which fit path an estimate resolved to. <see cref="FitsResident" /> is the historical (dense-model or
///     entirely-VRAM-resident) outcome; <see cref="FitsWithExpertOffload" /> is only reachable for a Mixture-of-Experts
///     model on a GPU-accelerated node whose non-expert weights + KV fit VRAM while its expert weights fit available RAM.
/// </summary>
public enum MoeFitVerdict
{
    /// <summary>All weights are resident in the scored budget (VRAM or RAM) — today's behavior.</summary>
    FitsResident = 0,

    /// <summary>Non-expert weights + KV + overhead fit VRAM; expert weights fit available RAM (llama.cpp <c>--n-cpu-moe</c>).</summary>
    FitsWithExpertOffload = 1,

    /// <summary>Neither the resident nor (when applicable) the expert-offload budget fits.</summary>
    DoesNotFit = 2
}

/// <summary>
///     Optional explicit attention geometry read from a GGUF header, preferred over the derived
///     <c>head_dim = embedding_length / n_heads</c> when present. All fields are optional; passing <see langword="null" />
///     (or an all-null record) to <see cref="MemoryFitEstimator.Estimate" /> preserves the legacy derived-head_dim,
///     no-sliding-window behavior exactly.
/// </summary>
/// <param name="KeyLength">
///     <c>{arch}.attention.key_length</c> — the per-head key dimension (e.g. 128 on Qwen3, 256 on Gemma3), overriding the
///     derived head_dim. Qwen3 pins head_dim independently of the embedding width, so the derivation under-estimates its KV.
/// </param>
/// <param name="ValueLength"><c>{arch}.attention.value_length</c> — the per-head value dimension.</param>
/// <param name="SlidingWindow">
///     <c>{arch}.attention.sliding_window</c> — the sliding-window size. A positive value marks interleaved sliding-window
///     attention; the window-limited layers' KV cache is capped at this many positions instead of the full context.
/// </param>
/// <param name="SlidingWindowPattern">
///     The global-attention stride: every Nth layer is full attention, the rest window-limited (6 for Gemma3's 5:1
///     local:global pattern, 2 for Gemma2). Resolved from the header or a per-arch default; <see langword="null" /> leaves
///     every layer full-attention (a conservative over-estimate).
/// </param>
public sealed record GgufAttentionShape(
    long? KeyLength = null,
    long? ValueLength = null,
    long? SlidingWindow = null,
    long? SlidingWindowPattern = null);

/// <summary>
///     Optional Mixture-of-Experts facts for a GGUF model. All fields are additive/optional — omitting this record
///     (passing <see langword="null" /> to <see cref="MemoryFitEstimator.Estimate" />) preserves the pre-existing
///     dense-model estimate exactly.
/// </summary>
/// <param name="ActiveParamCount">
///     Published/known active parameters per token (e.g. the "A3B" in "Qwen3.5-35B-A3B"), when available. Enables the
///     precise expert-share approximation; when <see langword="null" /> a conservative default share is used instead.
/// </param>
/// <param name="ExpertCount">Total experts (GGUF <c>{arch}.expert_count</c>). A positive value marks the model as MoE.</param>
/// <param name="ExpertUsedCount">Experts routed per token (GGUF <c>{arch}.expert_used_count</c>), e.g. 2 of 8 for a top-2 gate.</param>
public sealed record MoeFacts(long? ActiveParamCount, long? ExpertCount, long? ExpertUsedCount)
{
    /// <summary>True when <see cref="ExpertCount" /> is known and positive — a Mixture-of-Experts model.</summary>
    public bool IsMoe => ExpertCount is > 0;
}

/// <summary>
///     The result of a single memory-fit estimate. <see cref="HeadroomBytes" /> is <c>budget − estimated</c> (negative
///     when the model does not fit; for <see cref="MoeFitVerdict.FitsWithExpertOffload" /> it is the GPU/VRAM headroom
///     specifically, the binding constraint of that path), and <see cref="Mode" /> records which budget was used.
///     <see cref="GpuBytes" />/<see cref="CpuBytes" /> are only populated when <see cref="MoeVerdict" /> is
///     <see cref="MoeFitVerdict.FitsWithExpertOffload" />; otherwise both are <see langword="null" />.
///     <see cref="Confidence" /> flags whether the estimate leaned on a derived head_dim or file-size weights, and
///     <see cref="NativeQuantFormat" /> whether the quant is a native, non-requantizable format (MXFP4).
/// </summary>
public sealed record MemoryFitEstimate(
    bool Fits,
    long EstimatedBytes,
    long HeadroomBytes,
    FitMode Mode,
    MoeFitVerdict MoeVerdict,
    long? GpuBytes,
    long? CpuBytes,
    bool ExpertsOffloaded,
    FitConfidence Confidence = FitConfidence.Exact,
    bool NativeQuantFormat = false);
