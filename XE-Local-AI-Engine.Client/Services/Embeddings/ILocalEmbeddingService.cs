namespace XE_Local_AI_Engine.Client.Services.Embeddings;

using Microsoft.Extensions.AI;

public interface ILocalEmbeddingService
{
    Task<Embedding<float>> GenerateAsync(string value, CancellationToken cancellationToken = default);
}
