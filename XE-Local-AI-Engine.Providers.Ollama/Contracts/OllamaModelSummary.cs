namespace XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     One model installed on the Ollama runtime, as the XE-owned replacement for OllamaSharp's transport model.
/// </summary>
/// <remarks>
///     Every field after <see cref="Name" /> has a default so a test fake that only cares about the name can write
///     <c>new OllamaModelSummary(name)</c>. Production code always supplies every field: a default never means
///     "unknown" on a real listing.
/// </remarks>
public sealed class OllamaModelSummary
{
    /// <summary>
    ///     The resolved model name. Ollama's list endpoint fills <c>model</c> on current daemons and only <c>name</c> on
    ///     older ones; the provider resolves that here so no consumer has to know about the dual-name ambiguity.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The content digest the runtime reports, used as the classification cache key.</summary>
    public string Digest { get; init; } = "";

    /// <summary>On-disk weight size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Last-modified instant, normalized to UTC once by the provider.</summary>
    public DateTimeOffset ModifiedAtUtc { get; init; }

    /// <summary>Model family as reported in the runtime's details block, when it reports one.</summary>
    public string? Family { get; init; }

    /// <summary>Parameter count label (for example <c>8B</c>), when reported.</summary>
    public string? ParameterSize { get; init; }

    /// <summary>Quantization label (for example <c>Q4_K_M</c>), when reported.</summary>
    public string? QuantizationLevel { get; init; }
}
