namespace XE_Local_AI_Engine.Client.Services.Embeddings.Implementation;

using Microsoft.Extensions.AI;

public sealed class LocalEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : ILocalEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));

    public Task<Embedding<float>> GenerateAsync(string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return _embeddingGenerator.GenerateAsync(value, cancellationToken: cancellationToken);
    }
}
