namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The MoE/param/quant/context inputs the Inference Optimizer needs from a local GGUF file's header. A public seam so
///     the Application-layer orchestrator can read them without depending on the Hugging Face provider's INTERNAL GGUF
///     header reader. Unlike <see cref="GgufModelFootprintFacts" /> (which the capacity advisor consumes and which omits
///     the Mixture-of-Experts fields), this projection surfaces <see cref="ExpertCount" /> / <see cref="IsMoe" /> so a
///     persisted inference profile records whether the optimizer must measure MoE throughput empirically.
/// </summary>
public sealed class GgufModelMetadata
{
    /// <summary>Total parameter count from <c>general.parameter_count</c>, or <see langword="null" /> when absent.</summary>
    public required long? ParamCount { get; init; }

    /// <summary>The header's stringified <c>general.file_type</c> quant marker, or <see langword="null" /> when absent.</summary>
    public required string? QuantType { get; init; }

    /// <summary>The model's native maximum context length, or <see langword="null" /> when absent.</summary>
    public required long? ContextLength { get; init; }

    /// <summary>The declared expert count for an MoE model (clamped to <see cref="int" />), or <see langword="null" /> for a dense model.</summary>
    public required int? ExpertCount { get; init; }

    /// <summary>True when the GGUF declares a positive expert count.</summary>
    public required bool IsMoe { get; init; }
}

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
