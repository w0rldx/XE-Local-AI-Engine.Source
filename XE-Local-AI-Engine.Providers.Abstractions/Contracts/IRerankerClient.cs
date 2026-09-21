namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     A local cross-encoder reranking seam: it scores how well each candidate document answers a query and returns one
///     relevance score per document, aligned to the input order.
/// </summary>
/// <remarks>
///     Backed by a node-local reranking runtime (llama-server's <c>/v1/rerank</c> endpoint) wired at the composition
///     root, so knowledge retrieval depends only on this contract. Reranking is a retrieval-quality enhancement, never
///     a hard dependency. <strong>Privacy:</strong> the query and document text are sent only to the node-local runtime
///     and are NEVER logged (an implementation logs at most an exception type on failure).
/// </remarks>
public interface IRerankerClient
{
    /// <summary>
    ///     Scores each document in <paramref name="documents" /> against <paramref name="query" /> using the reranker
    ///     model named <paramref name="modelName" />, spawning/reusing the node-local reranking runtime for it.
    /// </summary>
    /// <param name="modelName">The installed reranker model name to score with; resolved to its runtime the same way an embedding model is.</param>
    /// <param name="documents">The candidate document contents to score, in caller order.</param>
    /// <returns>
    ///     A relevance score for each document, aligned one-to-one with <paramref name="documents" /> by index (higher = more relevant), or <see langword="null" /> when reranking is unavailable.
    /// </returns>
    /// <remarks>
    ///     Graceful degrade: when the reranker model is not installed, the runtime is down, the transport fails, or the response
    ///     is malformed, the result is <see langword="null" /> so the caller keeps its existing fusion order rather than failing
    ///     the search — exactly mirroring the embedding-arm degrade-to-lexical behavior. A cancellation requested by
    ///     <paramref name="cancellationToken" />, which is flowed through spawn and the HTTP call, propagates rather than degrading.
    /// </remarks>
    Task<IReadOnlyList<double>?> RerankAsync(string modelName,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken);
}
