namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.Extensions.Options;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Resolves the ACTUAL embedding model name to hand to a provider's embedding generator from the configured
///     <see cref="KnowledgeBaseOptions.EmbeddingModelName" /> and the models installed on the resolved provider. The
///     configured default is an Ollama-style name (for example <c>nomic-embed-text</c>); on a llama.cpp node the same
///     embedding weights are installed under a <c>&lt;repo&gt;:&lt;quant&gt;</c> GGUF name that never equals that
///     default, so a literal pass-through fails even though a matching embedding GGUF is present. This resolver bridges
///     that gap so knowledge-base embedding works out of the box on either runtime, while keeping the exact configured
///     name when it is installed (so an Ollama node with <c>nomic-embed-text</c> is unaffected). The chunk-vector and
///     query-vector lanes MUST share the same resolved name so the two vector sets stay comparable.
/// </summary>
public interface IEmbeddingModelResolver
{
    /// <summary>
    ///     Resolves the embedding model name to use on <paramref name="provider" />. Resolution order:
    ///     (1) the configured name when a case-insensitively equal model is installed;
    ///     (2) otherwise the first installed model whose NAME identifies an embedding model
    ///     (<see cref="ModelKindDetector.IsEmbeddingName" />), by ordinal-ignore-case name order;
    ///     (3) otherwise the configured name unchanged (the caller's graceful "not available" path then fires).
    ///     A transport failure while enumerating installed models degrades to (3) rather than throwing.
    /// </summary>
    Task<string> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class EmbeddingModelResolver : IEmbeddingModelResolver
{
    private readonly KnowledgeBaseOptions _options;

    public EmbeddingModelResolver(IOptions<KnowledgeBaseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<string> ResolveAsync(ILocalModelProvider provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var configuredName = _options.EmbeddingModelName;

        IReadOnlyList<LocalModelDescriptor> installed;
        try
        {
            installed = await provider.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OllamaException or InvalidOperationException)
        {
            // Provider process down / transport error / unmapped provider. Keep the configured name so the caller's
            // existing graceful "embedding model not available" path fires unchanged. No model or chunk text is involved.
            return configuredName;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An HttpClient/provider request TIMEOUT surfaces as TaskCanceledException (a OperationCanceledException
            // subtype) even though the CALLER never cancelled — the filter distinguishes the two: when the caller's
            // token was NOT the one that fired, this is a timeout, so degrade to the configured name like the other
            // transport failures above. A genuine caller cancellation (cancellationToken.IsCancellationRequested is
            // true) falls through this filter and rethrows, propagating as normal.
            return configuredName;
        }

        // (1) Exact configured name is installed → keep it (an Ollama node with nomic-embed-text is unaffected).
        var exactMatch = installed.FirstOrDefault(descriptor =>
            string.Equals(descriptor.ModelName, configuredName, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            return configuredName;
        }

        // (2) First installed embedding-named model in a deterministic order (for example a nomic-embed GGUF).
        var embeddingModel = installed
                             .Where(descriptor => !string.IsNullOrWhiteSpace(descriptor.ModelName)
                                                  && ModelKindDetector.IsEmbeddingName(descriptor.ModelName))
                             .OrderBy(descriptor => descriptor.ModelName, StringComparer.OrdinalIgnoreCase)
                             .FirstOrDefault();

        // (3) Nothing installed matches → keep the configured name (graceful failure downstream).
        return embeddingModel?.ModelName ?? configuredName;
    }
}
