namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Resolves the ACTUAL embedding model name to hand to a provider's embedding generator from the configured
///     <see cref="KnowledgeBaseOptions.EmbeddingModelName" /> and the models installed on the resolved provider.
/// </summary>
/// <remarks>
///     The configured default is an Ollama-style name such as <c>nomic-embed-text</c>; on a llama.cpp node the same
///     weights are installed under a <c>&lt;repo&gt;:&lt;quant&gt;</c> GGUF name that never equals it, so a literal
///     pass-through fails even with a matching embedding GGUF present. Bridging that gap makes knowledge-base embedding
///     work on either runtime, while an installed exact configured name is kept as-is. The chunk-vector and
///     query-vector lanes MUST share one resolved name so the two vector sets stay comparable.
/// </remarks>
public interface IEmbeddingModelResolver
{
    /// <summary>Resolves the embedding model name to use on <paramref name="provider" />.</summary>
    /// <remarks>
    ///     Resolution order: (1) the configured name when a case-insensitively equal model is installed, confident;
    ///     (2) otherwise the first installed model whose NAME identifies an embedding model
    ///     (<see cref="ModelKindDetector.IsEmbeddingName" />), by ordinal-ignore-case name order, confident;
    ///     (3) otherwise the configured name unchanged and NOT confident, so the caller's graceful not-available path
    ///     fires. A transport failure while enumerating installed models also degrades to (3), NOT confident, never throwing.
    /// </remarks>
    Task<EmbeddingModelResolution> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken);
}
