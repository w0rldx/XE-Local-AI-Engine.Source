namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Semantic retrieval arm of the hybrid search pipeline. Scores stored chunk vectors against a pre-embedded query
///     vector and returns the closest chunks. Implementations must only ever compare vectors built by the SAME embedding
///     model (M1): a same-dimension, different-model vector is incomparable and would rank as valid garbage. Scoped: an
///     implementation reads through the request-scoped <see cref="Persistence.NodeChatDbContext" /> connection.
/// </summary>
public interface IVectorSearch
{
    /// <summary>
    ///     Returns up to <paramref name="limit" /> chunks whose stored vector is closest to <paramref name="queryVector" />,
    ///     best first. Only vectors whose <c>embedding_model</c> equals <paramref name="embeddingModel" /> are considered.
    /// </summary>
    /// <param name="queryVector">The query embedding (already built via the query-intent prefix and the current model).</param>
    /// <param name="embeddingModel">The current embedding model; the model-scope filter key (M1).</param>
    /// <param name="limit">Maximum number of hits to return.</param>
    /// <param name="documentId">Optional scope: when set, only chunks of this document are considered.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryVector,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        int limit,
        Guid? documentId,
        CancellationToken cancellationToken) =>
        SearchAsync(queryVector,
            embeddingModel,
            vectorIdentity,
            vectorDimension,
            limit,
            documentId,
            KnowledgeCollectionScope.DefaultId,
            cancellationToken);

    /// <summary>Collection-scoped variant used by production retrieval. Implementations must enforce this boundary.</summary>
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(ReadOnlyMemory<float> queryVector,
        string embeddingModel,
        string vectorIdentity,
        int vectorDimension,
        int limit,
        Guid? documentId,
        string collectionId,
        CancellationToken cancellationToken);
}

/// <summary>One semantic match: the chunk, its owning document, and its cosine-similarity score (higher is better).</summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="DocumentId">Owning document identifier.</param>
/// <param name="Score">Cosine similarity in [-1, 1]; higher ranks higher.</param>
public sealed record VectorSearchHit(Guid ChunkId, Guid DocumentId, float Score);
