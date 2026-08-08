namespace XE_Local_AI_Engine.Tests.Caching;

using XE_Local_AI_Engine.Client.Common.Caching;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared embedding cache behind playbook-retrieval ranking, semantic memory dedup and knowledge-search query
///     embeddings. These assert the three properties the three hand-rolled caches it replaced did not have: eviction is
///     driven by the BYTE budget and picks the least-recently-USED victim (not the oldest inserted), entries expire on
///     their TTL, and concurrent callers missing on the same key share one computation instead of each paying a
///     round-trip to the single-slot embedding server.
/// </summary>
public sealed class ByteBudgetedCacheTests
{
    [Test]
    public void Set_WhenOverByteBudget_EvictsTheLeastRecentlyUsed_NotTheOldestInserted()
    {
        // Cost is the value itself, so the budget is exact and the entry bound can never be what bites.
        var cache = Cache(maxBytes: 30, maxEntries: 100);
        cache.Set("a", 10);
        cache.Set("b", 10);

        // Reading "a" makes "b" the coldest entry even though "a" was inserted first — the FIFO cache this replaced
        // would evict "a" here.
        AssertEx.True(cache.TryGet("a", out _), "The first entry is live before the budget is exceeded.");

        cache.Set("c", 15);

        AssertEx.True(cache.TryGet("a", out _), "The recently-read entry must survive eviction.");
        AssertEx.False(cache.TryGet("b", out _), "The least-recently-used entry is the victim.");
        AssertEx.True(cache.TryGet("c", out _), "The just-inserted entry is never evicted by its own insert.");
        AssertEx.True(cache.ApproximateSizeInBytes <= 30, $"Eviction must restore the budget, not exceed it ({cache.ApproximateSizeInBytes}).");
    }

    [Test]
    public void Set_WhenOneEntryIsHuge_EvictsEnoughEntriesToFitTheBudget()
    {
        var cache = Cache(maxBytes: 30, maxEntries: 100);
        cache.Set("a", 10);
        cache.Set("b", 10);
        cache.Set("c", 10);

        // A single wide-vector entry has to displace several narrow ones — the property an entry-count bound misses.
        cache.Set("wide", 28);

        AssertEx.Equal(expected: 1, cache.Count, "Only the just-inserted wide entry fits inside the byte budget.");
        AssertEx.True(cache.TryGet("wide", out _), "The just-inserted entry survives.");
    }

    [Test]
    public void TryGet_AfterTtlElapses_IsAMiss()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = Cache(maxBytes: 1024, maxEntries: 100, ttl: TimeSpan.FromSeconds(10), timeProvider: clock);
        cache.Set("a", 10);

        AssertEx.True(cache.TryGet("a", out _), "The entry is live inside its TTL.");
        clock.Advance(TimeSpan.FromSeconds(11));

        AssertEx.False(cache.TryGet("a", out _), "An entry past its TTL must be a miss.");
    }

    [Test]
    public async Task GetOrAddManyAsync_ConcurrentIdenticalMisses_ComputeTheKeyExactlyOnce()
    {
        const int callers = 8;
        var cache = Cache(maxBytes: 1024, maxEntries: 100);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var keysComputed = 0;

        var results = await RunConcurrentlyAsync(callers,
            async (missing, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                if (missing.Count == 0)
                {
                    // Every caller's factory runs — a caller whose keys are all cached or already in flight still has its
                    // own uncacheable work to do (the search query, the extraction candidates).
                    return [];
                }

                Interlocked.Add(ref keysComputed, missing.Count);

                // Hold the single owner inside the computation until every caller has claimed or queued behind it.
                await release.Task.ConfigureAwait(false);
                return [42];
            },
            cache,
            async () =>
            {
                await AssertEx.EventuallyAsync(() => Volatile.Read(ref factoryCalls) == callers,
                    TimeSpan.FromSeconds(10),
                    "Every concurrent caller must reach the computation stage before the owner completes.");
                release.SetResult();
            });

        AssertEx.Equal(expected: 1, Volatile.Read(ref keysComputed), "Concurrent misses on one key must be computed exactly once.");
        foreach (var result in results)
        {
            AssertEx.NotNull(result, "A coalesced caller must receive the owner's value, not a degrade.");
            AssertEx.Equal(expected: 42, result![0], "Every coalesced caller sees the same computed value.");
        }
    }

    [Test]
    public async Task GetOrAddManyAsync_WhenTheOwningCallerDegrades_WaitersDegradeInsteadOfHanging()
    {
        const int callers = 4;
        var cache = Cache(maxBytes: 1024, maxEntries: 100);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        var results = await RunConcurrentlyAsync(callers,
            async (missing, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                if (missing.Count == 0)
                {
                    return [];
                }

                await release.Task.ConfigureAwait(false);

                // The owner signals a degrade (a short/partial embedding response); nobody may be left waiting on it.
                return null;
            },
            cache,
            async () =>
            {
                await AssertEx.EventuallyAsync(() => Volatile.Read(ref factoryCalls) == callers,
                    TimeSpan.FromSeconds(10),
                    "Every concurrent caller must reach the computation stage before the owner degrades.");
                release.SetResult();
            });

        foreach (var result in results)
        {
            AssertEx.Null(result, "A caller coalesced onto a degraded computation must take its own degrade path.");
        }

        AssertEx.Equal(expected: 0, cache.Count, "A degrade caches nothing.");
    }

    [Test]
    public async Task GetOrAddManyAsync_WhenEveryKeyIsCached_StillInvokesTheFactoryOnce()
    {
        // The three call sites fold uncacheable work (the query, the candidates) into the same round-trip, so skipping
        // the invocation on a full-hit batch would cost them a second round-trip against a single-slot server.
        var cache = Cache(maxBytes: 1024, maxEntries: 100);
        cache.Set("a", 7);
        var missingCounts = new List<int>();

        var values = await cache.GetOrAddManyAsync(["a"],
            (missing, _) =>
            {
                missingCounts.Add(missing.Count);
                return Task.FromResult<IReadOnlyList<int>?>([]);
            },
            CancellationToken.None);

        AssertEx.NotNull(values, "A fully-cached batch resolves.");
        AssertEx.Equal(expected: 7, values![0], "The cached value is returned.");
        AssertEx.Equal(expected: 1, missingCounts.Count, "The factory is invoked exactly once.");
        AssertEx.Equal(expected: 0, missingCounts[0], "With everything cached the factory is handed no keys.");
    }

    // Runs `callers` identical single-key batches concurrently, letting `arrange` unblock them once they have all
    // claimed or queued, and returns each caller's result.
    private static async Task<IReadOnlyList<int[]?>> RunConcurrentlyAsync(int callers,
        Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<int>?>> computeMissing,
        ByteBudgetedCache<string, int> cache,
        Func<Task> arrange)
    {
        var calls = Enumerable.Range(0, callers)
                              .Select(_ => Task.Run(() => cache.GetOrAddManyAsync(["shared"], computeMissing, CancellationToken.None)))
                              .ToArray();

        await arrange();
        return await Task.WhenAll(calls);
    }

    private static ByteBudgetedCache<string, int> Cache(long maxBytes,
        int maxEntries,
        TimeSpan ttl = default,
        TimeProvider? timeProvider = null)
    {
        return new ByteBudgetedCache<string, int>(maxBytes, maxEntries, static (_, value) => value, ttl, timeProvider);
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
