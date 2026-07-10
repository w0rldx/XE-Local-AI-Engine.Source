namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fit;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Pure, I/O-free estimator of whether a GGUF model fits the node's memory budget. Implements the oobabooga
///     "GGUF VRAM formula" (<see href="https://oobabooga.github.io/blog/posts/gguf-vram-formula/" />):
///     <code>
///     total ≈ weights(quant) + KV_cache + ~0.75 GB CUDA/runtime overhead + safety margin
///     KV_cache = 2 · n_layers · n_kv_heads · head_dim · ctx · bytesPerKvElement(kvQuant)
///     </code>
///     The budget is the GPU VRAM when GPU acceleration is available and VRAM was measured
///     (<see cref="HardwareProfile.GpuAccelAvailable" /> &amp;&amp; <see cref="HardwareProfile.VramKnown" />); otherwise
///     the node's available RAM (the CPU-mode degrade rule). It performs no GGUF parsing — every header input is supplied
///     by the Hugging Face GGUF discovery per-file DTO. A model fits iff <c>total ≤ budget</c>.
/// </summary>
/// <remarks>
///     Singleton-safe (stateless). The <c>weights</c> term prefers the header param count × bytes-per-weight of the
///     chosen quant; when the param count is unavailable it falls back to the file's on-disk byte size (the quantized
///     weights already on disk), so a file is never rejected purely for a missing param count when its size is known.
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
    /// <param name="embeddingLength">Embedding length; <c>head_dim = embeddingLength / n_heads</c>.</param>
    /// <param name="attentionHeadCount">n_heads (<c>AttentionHeadCount</c>) — the divisor for head_dim.</param>
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
        KvCacheQuant? kvCacheQuant = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        ArgumentNullException.ThrowIfNull(profile);

        var weightsBytes = EstimateWeightsBytes(quant, paramCount, fileSizeBytes);
        var bytesPerKvElement = ResolveKvBytesPerElement(kvCacheQuantized, kvCacheQuant);
        var kvBytes = EstimateKvCacheBytes(blockCount, attentionHeadCountKV, embeddingLength, attentionHeadCount, ctxTarget, bytesPerKvElement);

        var useGpu = profile is { GpuAccelAvailable: true, VramKnown: true } && profile.VramBytes is > 0;
        var budgetBytes = useGpu ? profile.VramBytes!.Value : profile.AvailableRamBytes;
        var mode = useGpu ? FitMode.Gpu : FitMode.Cpu;

        // Apply the safety margin to the model-driven terms (weights + KV) only, then add the fixed runtime overhead.
        var marginBytes = (long)((weightsBytes + kvBytes) * _safetyMarginFraction);
        var residentEstimatedBytes = weightsBytes + kvBytes + marginBytes + OverheadBytes;
        var residentHeadroomBytes = budgetBytes - residentEstimatedBytes;

        if (residentEstimatedBytes <= budgetBytes)
        {
            return new MemoryFitEstimate(Fits: true, residentEstimatedBytes, residentHeadroomBytes, mode, MoeFitVerdict.FitsResident, GpuBytes: null, CpuBytes: null, ExpertsOffloaded: false);
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
                    ExpertsOffloaded: true);
            }
        }

        return new MemoryFitEstimate(Fits: false, residentEstimatedBytes, residentHeadroomBytes, mode, MoeFitVerdict.DoesNotFit, GpuBytes: null, CpuBytes: null, ExpertsOffloaded: false);
    }

    /// <summary>
    ///     Bytes-per-weight for a quant label (the dominant llama.cpp K-quants, I-quants, and legacy/full types). Unknown
    ///     labels fall back to the Q4_K_M density (~0.5625 bytes/weight ≈ 4.5 bits) — a conservative middle ground. The
    ///     I-quant (IQ*) bit-widths are the measured effective bpw from the llama.cpp Llama-3.1-8B quantize benchmark, so an
    ///     IQ file is sized at its true density instead of the legacy 4.5bpw default.
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

    private static long EstimateKvCacheBytes(long blockCount,
        long attentionHeadCountKV,
        long embeddingLength,
        long attentionHeadCount,
        long ctxTarget,
        double bytesPerElement)
    {
        if (blockCount <= 0 || attentionHeadCountKV <= 0 || embeddingLength <= 0 || attentionHeadCount <= 0 || ctxTarget <= 0)
        {
            return 0;
        }

        // head_dim = embedding_length / n_heads. KV cache stores key AND value (the leading factor 2).
        var headDim = embeddingLength / (double)attentionHeadCount;

        return (long)(2d * blockCount * attentionHeadCountKV * headDim * ctxTarget * bytesPerElement);
    }
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
/// </summary>
public sealed record MemoryFitEstimate(
    bool Fits,
    long EstimatedBytes,
    long HeadroomBytes,
    FitMode Mode,
    MoeFitVerdict MoeVerdict,
    long? GpuBytes,
    long? CpuBytes,
    bool ExpertsOffloaded);
