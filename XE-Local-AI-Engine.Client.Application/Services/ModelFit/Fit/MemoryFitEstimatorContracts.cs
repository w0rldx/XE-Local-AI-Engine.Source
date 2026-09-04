namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fit;

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
///     What the KV cache costs for one model geometry at one context target, with the element size it was computed at
///     named on the result. Produced by <see cref="MemoryFitEstimator.EstimateKvCacheFootprint" />.
/// </summary>
/// <param name="BytesAtContext">Total KV-cache bytes across every layer at the requested context, or <c>0</c> when the header cannot size it.</param>
/// <param name="BytesPerToken">
///     <paramref name="BytesAtContext" /> divided by the requested context — the average per-token cost across all
///     layers, so an interleaved sliding-window model reports what it actually pays rather than a full-attention figure.
/// </param>
/// <param name="Quant">
///     The element size the figures were computed at. REQUIRED on the result: an unlabelled KV byte count is ambiguous
///     by a factor of two between the fp16 ranking estimate and the <c>q8_0</c> chat launch.
/// </param>
/// <param name="HeadDimDerived">Whether head_dim was derived from <c>embedding_length / n_heads</c> rather than read explicitly.</param>
public readonly record struct KvCacheFootprint(long BytesAtContext, double BytesPerToken, KvCacheQuant Quant, bool HeadDimDerived);

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
/// <param name="KeyLengthMla">
///     <c>{arch}.attention.key_length_mla</c> — the latent key dimension of Multi-head Latent Attention. Together with
///     <paramref name="ValueLengthMla" /> it is llama.cpp's <c>is_mla()</c> test (both present and positive); under MLA
///     the cache is a single latent K tensor per layer and NO V tensor is allocated at all.
/// </param>
/// <param name="ValueLengthMla">
///     <c>{arch}.attention.value_length_mla</c> — the MLA latent value dimension. It takes part in detection only: no V
///     cache exists under MLA, so it contributes no bytes.
/// </param>
public sealed record GgufAttentionShape(
    long? KeyLength = null,
    long? ValueLength = null,
    long? SlidingWindow = null,
    long? SlidingWindowPattern = null,
    long? KeyLengthMla = null,
    long? ValueLengthMla = null)
{
    /// <summary>
    ///     True when the header declares BOTH positive MLA lengths — llama.cpp's <c>is_mla()</c>. The single detection
    ///     authority; no architecture name is ever consulted.
    /// </summary>
    public bool IsMla => KeyLengthMla is > 0 && ValueLengthMla is > 0;
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
