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

    private readonly long _overheadBytes;
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
        _overheadBytes = overheadBytes;
        _safetyMarginFraction = safetyMarginFraction;
    }

    /// <summary>The fixed runtime overhead this estimator adds to every estimate (the insufficient-metadata floor).</summary>
    public long OverheadBytes => _overheadBytes;

    /// <summary>
    ///     Estimates the memory footprint of a model with the given GGUF header metadata at <paramref name="ctxTarget" />
    ///     tokens against <paramref name="profile" />. <paramref name="kvCacheQuantized" /> selects an 8-bit KV cache
    ///     (1 byte/element) instead of the default fp16 (2 bytes/element), lowering the KV term so a larger model can fit.
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
    /// <param name="kvCacheQuantized">When <see langword="true" />, KV cache is 8-bit (1 byte/element) instead of fp16.</param>
    public MemoryFitEstimate Estimate(string quant,
        long? paramCount,
        long fileSizeBytes,
        long blockCount,
        long attentionHeadCountKV,
        long embeddingLength,
        long attentionHeadCount,
        long ctxTarget,
        HardwareProfile profile,
        bool kvCacheQuantized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        ArgumentNullException.ThrowIfNull(profile);

        var weightsBytes = EstimateWeightsBytes(quant, paramCount, fileSizeBytes);
        var kvBytes = EstimateKvCacheBytes(blockCount, attentionHeadCountKV, embeddingLength, attentionHeadCount, ctxTarget, kvCacheQuantized);

        // Apply the safety margin to the model-driven terms (weights + KV) only, then add the fixed runtime overhead.
        var marginBytes = (long)((weightsBytes + kvBytes) * _safetyMarginFraction);
        var estimatedBytes = weightsBytes + kvBytes + marginBytes + _overheadBytes;

        var useGpu = profile is { GpuAccelAvailable: true, VramKnown: true } && profile.VramBytes is > 0;
        var budgetBytes = useGpu ? profile.VramBytes!.Value : profile.AvailableRamBytes;
        var mode = useGpu ? FitMode.Gpu : FitMode.Cpu;

        var headroomBytes = budgetBytes - estimatedBytes;
        var fits = estimatedBytes <= budgetBytes;

        return new MemoryFitEstimate(fits, estimatedBytes, headroomBytes, mode);
    }

    /// <summary>
    ///     Bytes-per-weight for a quant label (the dominant llama.cpp K-quants + legacy/full types). Unknown labels fall
    ///     back to the Q4_K_M density (~0.5625 bytes/weight ≈ 4.5 bits) — a conservative middle ground.
    /// </summary>
    public static double BytesPerWeight(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);

        // Approximate effective bits-per-weight → bytes-per-weight. Sourced from llama.cpp quant type bit-widths.
        return quant.Trim().ToUpperInvariant() switch
        {
            "Q2_K" => 2.625d / 8d,
            "Q3_K_S" or "Q3_K_M" or "Q3_K_L" or "Q3_K" => 3.4375d / 8d,
            "Q4_0" or "Q4_1" => 4.5d / 8d,
            "Q4_K_S" or "Q4_K_M" or "Q4_K" => 4.5d / 8d,
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

    private static long EstimateKvCacheBytes(long blockCount,
        long attentionHeadCountKV,
        long embeddingLength,
        long attentionHeadCount,
        long ctxTarget,
        bool kvCacheQuantized)
    {
        if (blockCount <= 0 || attentionHeadCountKV <= 0 || embeddingLength <= 0 || attentionHeadCount <= 0 || ctxTarget <= 0)
        {
            return 0;
        }

        // head_dim = embedding_length / n_heads. KV cache stores key AND value (the leading factor 2).
        var headDim = embeddingLength / (double)attentionHeadCount;
        var bytesPerElement = kvCacheQuantized ? 1d : 2d;

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

/// <summary>
///     The result of a single memory-fit estimate. <see cref="HeadroomBytes" /> is <c>budget − estimated</c> (negative
///     when the model does not fit), and <see cref="Mode" /> records which budget was used.
/// </summary>
public sealed record MemoryFitEstimate(
    bool Fits,
    long EstimatedBytes,
    long HeadroomBytes,
    FitMode Mode);
