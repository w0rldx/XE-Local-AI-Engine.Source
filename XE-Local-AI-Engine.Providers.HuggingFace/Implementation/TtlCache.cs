namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Minimal thread-safe TTL cache keyed by string with a bounded, approximately-LRU eviction policy, used to avoid
///     re-fetching Hugging Face Hub search results, repo-blob listings and GGUF header reads on every advisor refresh.
/// </summary>
/// <remarks>
///     User-driven search keys would otherwise grow without bound, so the cache caps itself at <c>maxEntries</c> and evicts
///     approximately-LRU (see <see cref="EvictIfOverCapacity" />). <c>MemoryCache</c> is deliberately not used: it has no built-in async
///     single-flight, its factory being able to run more than once under a concurrent-miss race, and its <c>SizeLimit</c> compaction is a
///     heuristic percentage purge rather than strict LRU — neither composes with the deterministic <c>TimeProvider</c>-driven expiry
///     these caches rely on and their tests assert against.
/// </remarks>
internal sealed class TtlCache<TValue>
{
    private const int DefaultMaxEntries = 256;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private long _accessClock;
    private int _evicting;

    public TtlCache(TimeProvider timeProvider, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        _timeProvider = timeProvider;
        _maxEntries = maxEntries;
    }

    /// <summary>
    ///     Returns the cached value for <paramref name="key" /> when present and unexpired; otherwise invokes
    ///     <paramref name="factory" /> once, caches the result for <paramref name="ttl" />, and returns it.
    /// </summary>
    /// <remarks>
    ///     A per-key gate serializes concurrent misses so two callers racing a cold entry issue one fetch, not two, and expiry is
    ///     checked lazily on read, with no background sweep. A <paramref name="ttl" /> of zero or less disables caching for this call
    ///     (the factory always runs). A failed or cancelled factory caches nothing — the entry keeps its prior state (empty, or its
    ///     previous value).
    /// </remarks>
    public async Task<TValue> GetOrAddAsync(string key, TimeSpan ttl, Func<CancellationToken, Task<TValue>> factory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (ttl <= TimeSpan.Zero)
        {
            return await factory(ct).ConfigureAwait(false);
        }

        var entry = _entries.GetOrAdd(key, static _ => new CacheEntry());

        var result = default(TValue)!;
        var inserted = false;
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (entry.HasValue && entry.ExpiresAt > now)
            {
                Touch(entry);
                return entry.Value;
            }

            result = await factory(ct).ConfigureAwait(false);
            entry.Value = result;
            entry.ExpiresAt = now + ttl;
            entry.HasValue = true;
            Touch(entry);
            inserted = true;
        }
        finally
        {
            entry.Gate.Release();
        }

        // Evict only after a successful insert grew the live set, and outside the gate so a throwing factory (which leaves the entry untouched and rethrows above) never triggers a scan. Passing the
        // just-inserted key keeps it out of eviction while the cache is still over capacity.
        if (inserted)
        {
            EvictIfOverCapacity(key);
        }

        return result;
    }

    private void Touch(CacheEntry entry)
    {
        // A single monotonic counter gives a strict recency order across keys; the write is atomic under 32-bit too.
        Volatile.Write(ref entry.LastAccessStamp, Interlocked.Increment(ref _accessClock));
    }

    /// <summary>Brings the entry count back to <c>maxEntries</c> after an insert pushed it over.</summary>
    /// <remarks>
    ///     Expired entries are dropped first, then least-recently-used live ones, until the count is back at capacity.
    ///     Reads refresh recency via a cheap Interlocked stamp (no lock on the read path); this O(n) scan runs only on
    ///     the miss/insert path, and only while over capacity.
    /// </remarks>
    private void EvictIfOverCapacity(string justInsertedKey)
    {
        if (_entries.Count <= _maxEntries)
        {
            return;
        }

        // Serialize eviction so a burst of concurrent inserts doesn't each run an O(n) scan; a caller that finds an
        // eviction already in progress relies on the next over-capacity insert to retry.
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();

            // Pass 1: drop expired live entries (cheapest to lose). Never touch the just-inserted key, and skip any entry whose gate is held so an in-flight single-flight fetch is neither disrupted
            // nor duplicated. Removing a free entry another caller is re-adding is safe: that caller holds its own CacheEntry reference and completes against it; only single-flight briefly relaxes.
            foreach (var pair in _entries)
            {
                if (_entries.Count <= _maxEntries)
                {
                    return;
                }

                if (pair.Key == justInsertedKey || pair.Value.Gate.CurrentCount != 1)
                {
                    continue;
                }

                if (pair.Value.HasValue && pair.Value.ExpiresAt <= now)
                {
                    _entries.TryRemove(new KeyValuePair<string, CacheEntry>(pair.Key, pair.Value));
                }
            }

            if (_entries.Count <= _maxEntries)
            {
                return;
            }

            // Pass 2: evict the coldest free entries by access stamp until back at/below capacity. Never-touched
            // placeholders (e.g. an entry left empty by a throwing factory) carry stamp 0 and sort first.
            var coldest = _entries
                          .Where(pair => pair.Key != justInsertedKey && pair.Value.Gate.CurrentCount == 1)
                          .OrderBy(pair => Volatile.Read(ref pair.Value.LastAccessStamp))
                          .ToList();

            foreach (var pair in coldest)
            {
                if (_entries.Count <= _maxEntries)
                {
                    return;
                }

                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(pair.Key, pair.Value));
            }
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }

    private sealed class CacheEntry
    {
        // Recency stamp assigned from the cache-wide monotonic counter; a field (not a property) so it can be Volatile/Interlocked accessed by ref. An evicted entry's gate is intentionally not
        // disposed: a concurrent caller may already hold its reference, and the gate allocates no unmanaged handle here (we never touch AvailableWaitHandle), so the GC reclaims it safely.
        public long LastAccessStamp;

        public SemaphoreSlim Gate { get; } = new(initialCount: 1, maxCount: 1);
        public bool HasValue { get; set; }
        public TValue Value { get; set; } = default!;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
