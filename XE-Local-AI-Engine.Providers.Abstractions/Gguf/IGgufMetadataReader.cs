namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The MoE/param/quant/context inputs the Inference Optimizer needs from a local GGUF file's header. A public seam so
///     the Application-layer orchestrator can read them without depending on the Hugging Face provider's INTERNAL GGUF
///     header reader. Unlike <see cref="GgufModelFootprintFacts" /> (which the capacity advisor consumes and which omits
///     the Mixture-of-Experts fields), this projection surfaces <see cref="ExpertCount" /> / <see cref="IsMoe" /> so a
///     persisted inference profile records whether the optimizer must measure MoE throughput empirically.
/// </summary>
/// <param name="ParamCount">Total parameter count from <c>general.parameter_count</c>, or <see langword="null" /> when absent.</param>
/// <param name="QuantType">The header's stringified <c>general.file_type</c> quant marker, or <see langword="null" /> when absent.</param>
/// <param name="ContextLength">The model's native maximum context length, or <see langword="null" /> when absent.</param>
/// <param name="ExpertCount">The declared expert count for an MoE model (clamped to <see cref="int" />), or <see langword="null" /> for a dense model.</param>
/// <param name="IsMoe">True when the GGUF declares a positive expert count.</param>
public sealed record GgufModelMetadata(
    long? ParamCount,
    string? QuantType,
    long? ContextLength,
    int? ExpertCount,
    bool IsMoe);

/// <summary>
///     Reads the standardized header metadata of an INSTALLED, local GGUF file. Tolerant by contract: a missing file,
///     a non-GGUF payload, or a short read yields an all-<see langword="null" /> <see cref="GgufModelMetadata" /> rather
///     than throwing (cancellation excepted).
/// </summary>
public interface IGgufMetadataReader
{
    /// <summary>Reads <paramref name="filePath" />'s GGUF header and projects the optimizer's metadata inputs.</summary>
    Task<GgufModelMetadata> ReadMetadataAsync(string filePath, CancellationToken ct);
}
