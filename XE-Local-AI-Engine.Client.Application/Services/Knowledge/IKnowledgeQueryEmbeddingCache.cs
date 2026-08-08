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
    ///     Returns the cached query embedding for <paramref name="policyFamilyIdentity" /> + <paramref name="query" />
    ///     when present and unexpired. The caller validates the entry's exact identity and width against the current
    ///     policy before using it. Records a hit/miss metric.
    /// </summary>
    bool TryGet(string policyFamilyIdentity, string query, out KnowledgeQueryEmbeddingCacheEntry entry);

    /// <summary>Caches <paramref name="entry" /> for the policy family and query, evicting to stay within the size bound.</summary>
    void Store(string policyFamilyIdentity, string query, KnowledgeQueryEmbeddingCacheEntry entry);
}

/// <summary>
///     A cached transformed vector plus its exact canonical model/algorithm/version/width identity. The lookup key uses
///     the policy family because native width is not known until after the provider's first generation.
/// </summary>
public sealed record KnowledgeQueryEmbeddingCacheEntry(ReadOnlyMemory<float> Vector, string VectorIdentity);
