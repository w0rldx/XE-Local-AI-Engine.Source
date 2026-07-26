namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Bounded, RAM-only cache of recent knowledge-search query embeddings, keyed by the canonical vector identity
///     plus the exact query text. It lets a repeated query (the dominant retrieval latency is the embedding round trip)
///     skip re-embedding. The query text is never persisted or logged — it is reduced to a hash for the key, and the
///     vector lives only in process memory with a hard size bound and a short TTL, honoring the repo rule that embeddings
///     of potentially-sensitive text are never written to disk. The canonical identity means model, transform-policy, or
///     width changes can never return a stale vector.
/// </summary>
public interface IKnowledgeQueryEmbeddingCache
{
    /// <summary>
    ///     Returns the cached query vector for <paramref name="vectorIdentity" /> + <paramref name="query" /> when present
    ///     and unexpired. Records a hit/miss metric.
    /// </summary>
    bool TryGet(string vectorIdentity, string query, out ReadOnlyMemory<float> vector);

    /// <summary>Caches <paramref name="vector" /> for <paramref name="vectorIdentity" /> + <paramref name="query" />, evicting to stay within the size bound.</summary>
    void Store(string vectorIdentity, string query, ReadOnlyMemory<float> vector);
}
