namespace XE_Local_AI_Engine.Providers.Ollama.Implementation;

using Microsoft.Extensions.AI;
using OllamaSharp.Models.Exceptions;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Wraps the minted embedding generator so an OllamaSharp transport failure surfaces as the provider-owned
///     <see cref="OllamaUnavailableException" /> instead of an SDK type.
/// </summary>
/// <remarks>
///     <see cref="HttpRequestException" /> and <see cref="OllamaException" /> are the two shapes OllamaSharp's
///     transport actually throws for an absent or failing daemon. Anything else (an argument error, a caller
///     cancellation) passes through untouched. Disposal is left to the base type, which owns the inner generator.
/// </remarks>
internal sealed class OllamaExceptionTranslatingEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    public OllamaExceptionTranslatingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> innerGenerator)
        : base(innerGenerator)
    {
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or OllamaException)
        {
            throw new OllamaUnavailableException("The Ollama daemon could not be reached for an embedding request.", exception);
        }
    }
}
