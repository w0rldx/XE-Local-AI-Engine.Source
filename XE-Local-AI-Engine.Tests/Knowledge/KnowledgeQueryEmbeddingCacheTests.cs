namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The query-embedding cache is a bounded, RAM-only, TTL'd store keyed by (resolved model, query). These tests assert
///     a stored vector is returned on a matching lookup, that a different model is a distinct key (no stale cross-model
///     vector), that entries expire after the TTL, that the size bound evicts the coldest entry, and that a zero TTL
///     disables caching entirely.
/// </summary>
public sealed class KnowledgeQueryEmbeddingCacheTests
{
    private static readonly float[] VectorA = [0.1f, 0.2f, 0.3f];
    private static readonly float[] VectorB = [0.9f, 0.8f, 0.7f];

    [Test]
    public void Store_ThenTryGet_ReturnsTheCachedVector()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 300), new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        cache.Store("nomic-embed-text", "what is the retention policy", VectorA);
        var hit = cache.TryGet("nomic-embed-text", "what is the retention policy", out var vector);

        AssertEx.True(hit, "A stored query must be a cache hit.");
        AssertEx.True(vector.Span.SequenceEqual(VectorA), "The cached vector must round-trip unchanged.");
    }

    [Test]
    public void TryGet_ForADifferentModel_IsAMiss()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 300), new MutableTimeProvider(DateTimeOffset.UnixEpoch));
        cache.Store("model-a", "same query text", VectorA);

        var hit = cache.TryGet("model-b", "same query text", out _);

        AssertEx.False(hit, "A different resolved model is a distinct key and must not return the other model's vector.");
    }

    [Test]
    public void TryGet_AfterTtlElapses_IsAMiss()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 10), clock);
        cache.Store("model", "query", VectorA);

        clock.Advance(TimeSpan.FromSeconds(11));

        AssertEx.False(cache.TryGet("model", "query", out _), "An entry past its TTL must be a miss.");
    }

    [Test]
    public void Store_WhenOverCapacity_EvictsTheColdestEntry()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 1, ttlSeconds: 300), new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        cache.Store("model", "first", VectorA);
        cache.Store("model", "second", VectorB);

        AssertEx.False(cache.TryGet("model", "first", out _), "The coldest entry must be evicted once the bound is exceeded.");
        AssertEx.True(cache.TryGet("model", "second", out var vector) && vector.Span.SequenceEqual(VectorB), "The most-recent entry must survive.");
    }

    [Test]
    public void Store_WhenTtlIsZero_DisablesCaching()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 0), new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        cache.Store("model", "query", VectorA);

        AssertEx.False(cache.TryGet("model", "query", out _), "A zero TTL must disable the cache (every query re-embeds).");
    }

    private static IOptions<KnowledgeBaseOptions> Options(int maxEntries, int ttlSeconds)
    {
        return Microsoft.Extensions.Options.Options.Create(new KnowledgeBaseOptions
        {
            QueryEmbeddingCacheMaxEntries = maxEntries,
            QueryEmbeddingCacheTtlSeconds = ttlSeconds
        });
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
        }
    }
}
