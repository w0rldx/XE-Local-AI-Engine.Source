namespace XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     A local cross-encoder reranking seam. Given a query and a candidate document set, it scores how well each
///     document answers the query and returns a relevance score per document (aligned to the input order). Backed by a
///     node-local reranking runtime (llama-server's <c>/v1/rerank</c> endpoint); the concrete implementation is wired at
///     the composition root, so knowledge retrieval depends only on this contract.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Graceful degrade:</strong> reranking is a retrieval-quality enhancement, never a hard dependency. When
///         the reranker model is not installed, the runtime is down, the transport fails, or the response is malformed,
///         the implementation returns <see langword="null" /> so the caller keeps its existing fusion order rather than
///         failing the search — exactly mirroring the embedding-arm degrade-to-lexical behavior.
///     </para>
///     <para>
///         <strong>Privacy:</strong> the query and document text are sent only to the node-local runtime and are NEVER
///         logged (an implementation logs at most an exception type on failure).
///     </para>
/// </remarks>
public interface IRerankerClient
{
    /// <summary>
    ///     Scores each document in <paramref name="documents" /> against <paramref name="query" /> using the reranker
    ///     model named <paramref name="modelName" />, spawning/reusing the node-local reranking runtime for it.
    /// </summary>
    /// <param name="modelName">The installed reranker model name to score with; resolved to its runtime the same way an embedding model is.</param>
    /// <param name="query">The search query the documents are scored against.</param>
    /// <param name="documents">The candidate document contents to score, in caller order.</param>
    /// <param name="cancellationToken">Flowed through spawn and the HTTP call.</param>
    /// <returns>
    ///     A relevance score for each document, aligned one-to-one with <paramref name="documents" /> by index (higher =
    ///     more relevant), or <see langword="null" /> when reranking is unavailable so the caller degrades to its existing
    ///     order. A cancellation requested by <paramref name="cancellationToken" /> propagates rather than degrading.
    /// </returns>
    Task<IReadOnlyList<double>?> RerankAsync(string modelName,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken cancellationToken);
}
