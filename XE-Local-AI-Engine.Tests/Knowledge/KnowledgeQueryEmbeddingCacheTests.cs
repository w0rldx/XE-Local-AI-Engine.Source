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

        var expected = Entry(VectorA, "nomic-embed-text::native:v1:3");
        cache.Store("nomic-embed-text::native:v1", "what is the retention policy", expected);
        var hit = cache.TryGet("nomic-embed-text::native:v1", "what is the retention policy", out var entry);

        AssertEx.True(hit, "A stored query must be a cache hit.");
        AssertEx.Equal(expected.VectorIdentity, entry.VectorIdentity);
        AssertEx.True(entry.Vector.Span.SequenceEqual(VectorA), "The cached vector must round-trip unchanged.");
    }

    [Test]
    public void TryGet_ForADifferentModel_IsAMiss()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 300), new MutableTimeProvider(DateTimeOffset.UnixEpoch));
        cache.Store("model-a::native:v1", "same query text", Entry(VectorA, "model-a::native:v1:3"));

        var hit = cache.TryGet("model-b::native:v1", "same query text", out _);

        AssertEx.False(hit, "A different resolved model is a distinct key and must not return the other model's vector.");
    }

    [Test]
    public void TryGet_ForSameModelButDifferentTransformIdentity_IsAMiss()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 300), new MutableTimeProvider(DateTimeOffset.UnixEpoch));
        const string nativeIdentity = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M::native:v1:768";
        const string matryoshkaIdentity =
            "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M::layernorm-population-eps1e-5-truncate-l2:v1:512";
        cache.Store("nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M::native:v1",
            "same query text",
            Entry(VectorA, nativeIdentity));

        var hit = cache.TryGet(matryoshkaIdentity, "same query text", out _);

        AssertEx.False(hit, "Native and 512-wide vectors for one resolved model must occupy distinct cache keys.");
    }

    [Test]
    public void TryGet_AfterTtlElapses_IsAMiss()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 10), clock);
        cache.Store("model::native:v1", "query", Entry(VectorA, "model::native:v1:3"));

        clock.Advance(TimeSpan.FromSeconds(11));

        AssertEx.False(cache.TryGet("model::native:v1", "query", out _), "An entry past its TTL must be a miss.");
    }

    [Test]
    public void Store_WhenOverCapacity_EvictsTheColdestEntry()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 1, ttlSeconds: 300), new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        cache.Store("model::native:v1", "first", Entry(VectorA, "model::native:v1:3"));
        cache.Store("model::native:v1", "second", Entry(VectorB, "model::native:v1:3"));

        AssertEx.False(cache.TryGet("model::native:v1", "first", out _), "The coldest entry must be evicted once the bound is exceeded.");
        AssertEx.True(cache.TryGet("model::native:v1", "second", out var entry) && entry.Vector.Span.SequenceEqual(VectorB),
            "The most-recent entry must survive.");
    }

    [Test]
    public void Store_WhenTtlIsZero_DisablesCaching()
    {
        var cache = new KnowledgeQueryEmbeddingCache(Options(maxEntries: 8, ttlSeconds: 0), new MutableTimeProvider(DateTimeOffset.UnixEpoch));

        cache.Store("model::native:v1", "query", Entry(VectorA, "model::native:v1:3"));

        AssertEx.False(cache.TryGet("model::native:v1", "query", out _), "A zero TTL must disable the cache (every query re-embeds).");
    }

    private static KnowledgeQueryEmbeddingCacheEntry Entry(ReadOnlyMemory<float> vector, string identity) =>
        new(vector, identity);

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
