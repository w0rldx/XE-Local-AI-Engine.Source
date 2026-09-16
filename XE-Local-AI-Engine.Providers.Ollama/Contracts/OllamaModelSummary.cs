namespace XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     One model installed on the Ollama runtime, as the XE-owned replacement for OllamaSharp's transport model.
/// </summary>
/// <param name="Name">
///     The resolved model name. Ollama's list endpoint fills <c>model</c> on current daemons and only <c>name</c> on
///     older ones; the provider resolves that here so no consumer has to know about the dual-name ambiguity.
/// </param>
/// <param name="Digest">The content digest the runtime reports, used as the classification cache key.</param>
/// <param name="SizeBytes">On-disk weight size in bytes.</param>
/// <param name="ModifiedAtUtc">Last-modified instant, normalized to UTC once by the provider.</param>
/// <param name="Family">Model family as reported in the runtime's details block, when it reports one.</param>
/// <param name="ParameterSize">Parameter count label (for example <c>8B</c>), when reported.</param>
/// <param name="QuantizationLevel">Quantization label (for example <c>Q4_K_M</c>), when reported.</param>
/// <remarks>
///     Every field after <paramref name="Name" /> has a default so a test fake that only cares about the name can write
///     <c>new OllamaModelSummary(name)</c>. Production code always supplies every field: a default never means
///     "unknown" on a real listing.
/// </remarks>
public sealed record OllamaModelSummary(
    string Name,
    string Digest = "",
    long SizeBytes = 0,
    DateTimeOffset ModifiedAtUtc = default,
    string? Family = null,
    string? ParameterSize = null,
    string? QuantizationLevel = null);
