namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The bounded TTL cache honours TTL expiry and single-flight per key, and once over capacity evicts expired then
///     least-recently-used entries while preserving recently-read keys and never caching a failed factory result.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TtlCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    [Test]
    public async Task GetOrAddAsync_WhenCachedWithinTtl_InvokesFactoryOnce()
    {
        var clock = new AdvanceableClock();
        var cache = new TtlCache<int>(clock);
        var calls = 0;

        var first = await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(Interlocked.Increment(ref calls)), CancellationToken.None);
        var second = await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(Interlocked.Increment(ref calls)), CancellationToken.None);

        AssertEx.Equal(1, first);
        AssertEx.Equal(1, second);
        AssertEx.Equal(1, calls);
    }

    [Test]
    public async Task GetOrAddAsync_WhenEntryExpired_RecomputesValue()
    {
        var clock = new AdvanceableClock();
        var cache = new TtlCache<int>(clock);
        var calls = 0;

        await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(Interlocked.Increment(ref calls)), CancellationToken.None);
        clock.Advance(Ttl + TimeSpan.FromSeconds(1));
        var second = await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(Interlocked.Increment(ref calls)), CancellationToken.None);

        AssertEx.Equal(2, second);
        AssertEx.Equal(2, calls);
    }

    [Test]
    public async Task GetOrAddAsync_WhenTtlNonPositive_AlwaysInvokesFactory()
    {
        var cache = new TtlCache<int>(new AdvanceableClock());
        var calls = 0;

        await cache.GetOrAddAsync("k", TimeSpan.Zero, _ => Task.FromResult(Interlocked.Increment(ref calls)), CancellationToken.None);
        await cache.GetOrAddAsync("k", TimeSpan.Zero, _ => Task.FromResult(Interlocked.Increment(ref calls)), CancellationToken.None);

        AssertEx.Equal(2, calls);
    }

    [Test]
    public async Task GetOrAddAsync_WhenOverCapacity_EvictsLeastRecentlyUsedNotMostRecent()
    {
        var cache = new TtlCache<string>(new AdvanceableClock(), maxEntries: 3);

        await Insert(cache, "a");
        await Insert(cache, "b");
        await Insert(cache, "c");
        await Insert(cache, "d"); // pushes count to 4 -> evicts coldest ("a").

        // Probe surviving keys first: a hit does not mutate the cache, whereas the miss probe below re-inserts its
        // sentinel and can trigger another eviction, so the absent-key check must come last.
        AssertEx.True(await IsCached(cache, "b"));
        AssertEx.True(await IsCached(cache, "c"));
        AssertEx.True(await IsCached(cache, "d"));
        AssertEx.False(await IsCached(cache, "a"), "the least-recently-used key must be evicted.");
    }

    [Test]
    public async Task GetOrAddAsync_WhenKeyReadBeforeEviction_KeepsWarmKeyAndEvictsColderOne()
    {
        var cache = new TtlCache<string>(new AdvanceableClock(), maxEntries: 3);

        await Insert(cache, "a");
        await Insert(cache, "b");
        await Insert(cache, "c");

        // Read "a" so it becomes the most-recently-used; "b" is now the coldest.
        AssertEx.True(await IsCached(cache, "a"));

        await Insert(cache, "d"); // count 4 -> evict coldest ("b").

        AssertEx.True(await IsCached(cache, "a"), "a read must refresh recency and protect the key.");
        AssertEx.True(await IsCached(cache, "c"));
        AssertEx.True(await IsCached(cache, "d"));
        AssertEx.False(await IsCached(cache, "b"), "the colder key must be evicted instead.");
    }

    [Test]
    public async Task GetOrAddAsync_WhenOverCapacity_PrefersExpiredEntriesOverColderLiveOnes()
    {
        var clock = new AdvanceableClock();
        var cache = new TtlCache<string>(clock, maxEntries: 2);

        await Insert(cache, "live", TimeSpan.FromHours(1)); // older but long-lived.
        await Insert(cache, "stale", TimeSpan.FromMinutes(1)); // newer but short-lived.
        clock.Advance(TimeSpan.FromMinutes(2)); // "stale" now expired; "live" still valid.

        await Insert(cache, "fresh", TimeSpan.FromHours(1)); // count 3 -> expired "stale" evicted first.

        AssertEx.True(await IsCached(cache, "live"), "the colder live entry survives because expired is preferred.");
        AssertEx.True(await IsCached(cache, "fresh"));
        AssertEx.False(await IsCached(cache, "stale"), "an expired entry must be evicted before a colder live one.");
    }

    [Test]
    public async Task GetOrAddAsync_WhenExistingKeyReAddedAfterExpiry_ReplacesValueWithoutGrowing()
    {
        var clock = new AdvanceableClock();
        var cache = new TtlCache<string>(clock, maxEntries: 3);

        await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult("v1"), CancellationToken.None);
        clock.Advance(Ttl + TimeSpan.FromSeconds(1));
        var replaced = await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult("v2"), CancellationToken.None);

        AssertEx.Equal("v2", replaced);
        var cached = await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult("v3"), CancellationToken.None);
        AssertEx.Equal("v2", cached, "the re-added entry reuses the same key rather than adding a second one.");
    }

    [Test]
    public async Task GetOrAddAsync_WhenConcurrentMissesForSameKey_InvokesFactoryOnce()
    {
        var cache = new TtlCache<int>(new AdvanceableClock());
        var calls = 0;
        var release = new TaskCompletionSource();

        async Task<int> Factory(CancellationToken _)
        {
            var count = Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            return count;
        }

        var racers = Enumerable
                     .Range(0, 16)
                     .Select(_ => cache.GetOrAddAsync("k", Ttl, Factory, CancellationToken.None))
                     .ToArray();

        release.SetResult();
        var results = await Task.WhenAll(racers);

        AssertEx.Equal(1, calls);
        AssertEx.True(results.All(r => r == 1), "every racer observes the single flighted value.");
    }

    [Test]
    public async Task GetOrAddAsync_WhenFactoryThrows_CachesNothing()
    {
        var cache = new TtlCache<int>(new AdvanceableClock());
        var calls = 0;

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrAddAsync("k", Ttl, _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("boom");
            }, CancellationToken.None));

        var recovered = await cache.GetOrAddAsync("k", Ttl, _ => Task.FromResult(42), CancellationToken.None);

        AssertEx.Equal(42, recovered, "a throwing factory must leave nothing cached, so the next call recomputes.");
        AssertEx.Equal(1, calls, "the throwing factory ran exactly once and was not memoised.");
    }

    private static Task Insert(TtlCache<string> cache, string key, TimeSpan? ttl = null)
    {
        return cache.GetOrAddAsync(key, ttl ?? Ttl, _ => Task.FromResult(key), CancellationToken.None);
    }

    private static async Task<bool> IsCached(TtlCache<string> cache, string key)
    {
        // A cache hit returns the stored value without invoking the factory; a miss would run the sentinel factory.
        var value = await cache.GetOrAddAsync(key, Ttl, _ => Task.FromResult(Miss), CancellationToken.None);
        return !string.Equals(value, Miss, StringComparison.Ordinal);
    }

    private const string Miss = "\0__miss__";

    private sealed class AdvanceableClock : TimeProvider
    {
        private long _utcTicks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        }

        public void Advance(TimeSpan delta)
        {
            Interlocked.Add(ref _utcTicks, delta.Ticks);
        }
    }
}
