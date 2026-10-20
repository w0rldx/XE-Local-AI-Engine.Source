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
    ///     (1) The stored pin, else the configured name, when an equal model (ignoring case) is installed: confident. A missing
    ///     pin stops here, NOT confident with <see cref="EmbeddingModelResolution.PinnedModelMissing" />. (2) With nothing
    ///     pinned, the first installed <see cref="ModelKindDetector.IsEmbeddingName" /> model by ordinal-ignore-case name:
    ///     confident. (3) Otherwise the configured name, NOT confident, so the caller's not-available path fires; a transport
    ///     failure while listing models also degrades to (3), never throwing.
    /// </remarks>
    Task<EmbeddingModelResolution> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken);
}
