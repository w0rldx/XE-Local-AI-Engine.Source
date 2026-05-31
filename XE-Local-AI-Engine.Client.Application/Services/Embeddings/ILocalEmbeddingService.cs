namespace XE_Local_AI_Engine.Client.Services.Embeddings;

using Microsoft.Extensions.AI;

/// <summary>
///     Node-local embedding generation boundary used by application services that need vector representations.
/// </summary>
public interface ILocalEmbeddingService
{
    /// <summary>
    ///     Generates an embedding for a non-empty text value through the configured local embedding provider.
    /// </summary>
    Task<Embedding<float>> GenerateAsync(string value, CancellationToken cancellationToken = default);
}
