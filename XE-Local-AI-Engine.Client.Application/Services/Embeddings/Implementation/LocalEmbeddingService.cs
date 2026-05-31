namespace XE_Local_AI_Engine.Client.Services.Embeddings.Implementation;

using Microsoft.Extensions.AI;

/// <summary>
///     Thin adapter over the configured <see cref="IEmbeddingGenerator{TInput,TEmbedding}" />.
/// </summary>
/// <remarks>
///     Keeping this service narrow makes embedding-provider swaps a composition-root concern while preserving a
///     simple text-in/vector-out contract for node application code.
/// </remarks>
public sealed class LocalEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : ILocalEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));

    /// <inheritdoc />
    public Task<Embedding<float>> GenerateAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return _embeddingGenerator.GenerateAsync(value, cancellationToken: cancellationToken);
    }
}
