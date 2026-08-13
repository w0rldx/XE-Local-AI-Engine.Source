namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class KnowledgeChunkEmbeddingCacheTests
{
    private const string VectorIdentity = "nomic::native:v1:2";

    [Test]
    public async Task GetOrCreateManyAsync_DurableExactMatch_SkipsFactoryAndReturnsDefensiveCopy()
    {
        var key = Key('a');
        var durableVector = Vector(1f, 2f);
        var store = new StubReuseStore(new Dictionary<KnowledgeChunkEmbeddingCacheKey, byte[]> { [key] = durableVector });
        var cache = CreateCache(store);
        var factoryCalls = 0;

        var first = await cache.GetOrCreateManyAsync([key], (_, _) =>
        {
            factoryCalls++;
            return Task.FromResult<IReadOnlyList<byte[]>>([Vector(9f, 9f)]);
        }, CancellationToken.None).ConfigureAwait(false);
        first[0][0] = 0;
        var second = await cache.GetOrCreateManyAsync([key], (_, _) => throw new InvalidOperationException("must not run"), CancellationToken.None)
                                .ConfigureAwait(false);

        AssertEx.Equal(0, factoryCalls, "An exact committed vector must avoid embedding generation.");
        AssertEx.Equal(1, store.CallCount, "The hot working set must avoid a second durable lookup.");
        AssertEx.True(second[0].SequenceEqual(durableVector), "Returned vectors must be defensive copies of cached data.");
    }

    [Test]
    public async Task GetOrCreateManyAsync_VersionOrVectorIdentityChanges_DoNotReuseDurableVector()
    {
        var storedKey = Key('b');
        var changedKeys = new[]
        {
            new KnowledgeChunkEmbeddingCacheKey(storedKey.EmbeddingInputHash, "parser-v2", storedKey.ChunkerVersion, storedKey.VectorIdentity, storedKey.Dimension),
            new KnowledgeChunkEmbeddingCacheKey(storedKey.EmbeddingInputHash, storedKey.ParserVersion, "chunker-v2", storedKey.VectorIdentity, storedKey.Dimension),
            new KnowledgeChunkEmbeddingCacheKey(storedKey.EmbeddingInputHash, storedKey.ParserVersion, storedKey.ChunkerVersion, "other::native:v1:2", storedKey.Dimension),
            new KnowledgeChunkEmbeddingCacheKey(storedKey.EmbeddingInputHash, storedKey.ParserVersion, storedKey.ChunkerVersion, "other::native:v1:3", 3)
        };
        var store = new StubReuseStore(new Dictionary<KnowledgeChunkEmbeddingCacheKey, byte[]> { [storedKey] = Vector(1f, 2f) });
        var cache = CreateCache(store);
        IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>? factoryKeys = null;

        var result = await cache.GetOrCreateManyAsync(changedKeys, (keys, _) =>
        {
            factoryKeys = keys;
            return Task.FromResult<IReadOnlyList<byte[]>>(keys.Select(key => new byte[key.Dimension * sizeof(float)]).ToArray());
        }, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(changedKeys.Length, factoryKeys?.Count ?? 0,
            "Parser, chunker, vector identity, and dimension changes must each invalidate reuse.");
        AssertEx.Equal(changedKeys.Length, result.Count);
    }

    [Test]
    public async Task GetOrCreateManyAsync_FactoryThrows_DoesNotCacheFailure()
    {
        var key = Key('c');
        var cache = CreateCache(new StubReuseStore());
        var attempts = 0;
        Func<IReadOnlyList<KnowledgeChunkEmbeddingCacheKey>, CancellationToken, Task<IReadOnlyList<byte[]>>> factory = (_, _) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new IOException("provider unavailable");
            }

            return Task.FromResult<IReadOnlyList<byte[]>>([Vector(3f, 4f)]);
        };

        _ = await AssertEx.ThrowsAsync<IOException>(() => cache.GetOrCreateManyAsync([key], factory, CancellationToken.None))
                          .ConfigureAwait(false);
        var result = await cache.GetOrCreateManyAsync([key], factory, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(2, attempts, "A failed factory must leave the key unresolved for the next attempt.");
        AssertEx.True(result[0].SequenceEqual(Vector(3f, 4f)));
    }

    [Test]
    public async Task GetOrCreateManyAsync_AfterTtl_ResolvesAgain()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var key = Key('d');
        var store = new StubReuseStore();
        var cache = CreateCache(store, maxEntries: 8, ttlSeconds: 10, clock);
        var factoryCalls = 0;
        Task<IReadOnlyList<byte[]>> Factory(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> _, CancellationToken __)
        {
            factoryCalls++;
            return Task.FromResult<IReadOnlyList<byte[]>>([Vector(factoryCalls, 0f)]);
        }

        _ = await cache.GetOrCreateManyAsync([key], Factory, CancellationToken.None).ConfigureAwait(false);
        clock.Advance(TimeSpan.FromSeconds(11));
        var afterExpiry = await cache.GetOrCreateManyAsync([key], Factory, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(2, factoryCalls, "An expired hot entry must be regenerated when no durable match exists.");
        AssertEx.Equal(2, store.CallCount, "TTL expiry must also re-check the durable committed-vector layer.");
        AssertEx.True(afterExpiry[0].SequenceEqual(Vector(2f, 0f)));
    }

    [Test]
    public async Task GetOrCreateManyAsync_OverEntryCapacity_EvictsColdestKey()
    {
        var store = new StubReuseStore();
        var cache = CreateCache(store, maxEntries: 1);
        var first = Key('e');
        var second = Key('f');
        var calls = 0;
        Task<IReadOnlyList<byte[]>> Factory(IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys, CancellationToken _)
        {
            calls += keys.Count;
            return Task.FromResult<IReadOnlyList<byte[]>>(keys.Select(_ => Vector(calls, 0f)).ToArray());
        }

        _ = await cache.GetOrCreateManyAsync([first], Factory, CancellationToken.None).ConfigureAwait(false);
        _ = await cache.GetOrCreateManyAsync([second], Factory, CancellationToken.None).ConfigureAwait(false);
        _ = await cache.GetOrCreateManyAsync([first], Factory, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(3, calls, "The coldest key must be regenerated after the entry bound evicts it.");
    }

    [Test]
    public void CacheKey_RejectsRawTextInsteadOfAContentHash()
    {
        _ = AssertEx.Throws<ArgumentException>(() =>
            _ = new KnowledgeChunkEmbeddingCacheKey("confidential chunk text", "parser-v1", "chunker-v1", VectorIdentity, 2));
    }

    private static KnowledgeChunkEmbeddingCache CreateCache(StubReuseStore store,
        int maxEntries = 8,
        int ttlSeconds = 300,
        TimeProvider? clock = null)
    {
        return new KnowledgeChunkEmbeddingCache(store,
            Options.Create(new KnowledgeBaseOptions
            {
                ChunkEmbeddingCacheMaxEntries = maxEntries,
                ChunkEmbeddingCacheMaxMegabytes = 1,
                ChunkEmbeddingCacheTtlSeconds = ttlSeconds
            }),
            clock ?? TimeProvider.System);
    }

    private static KnowledgeChunkEmbeddingCacheKey Key(char hexadecimalDigit)
    {
        return new KnowledgeChunkEmbeddingCacheKey(new string(hexadecimalDigit, 64), "parser-v1", "chunker-v1", VectorIdentity, 2);
    }

    private static byte[] Vector(float first, float second)
    {
        var values = new[] { first, second };
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
    }

    private sealed class StubReuseStore(IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>? entries = null)
        : IKnowledgeChunkEmbeddingReuseStore
    {
        private readonly IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]> _entries =
            entries ?? new Dictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>();

        public int CallCount { get; private set; }

        public Task<IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]>> FindManyAsync(
            IReadOnlyList<KnowledgeChunkEmbeddingCacheKey> keys,
            DateTimeOffset notBeforeUtc,
            CancellationToken cancellationToken)
        {
            CallCount++;
            IReadOnlyDictionary<KnowledgeChunkEmbeddingCacheKey, byte[]> found = keys.Where(_entries.ContainsKey)
                .ToDictionary(key => key, key => _entries[key]);
            return Task.FromResult(found);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
