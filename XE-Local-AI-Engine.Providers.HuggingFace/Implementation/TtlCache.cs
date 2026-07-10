namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Minimal thread-safe TTL cache keyed by string, used to avoid re-fetching Hugging Face Hub search results,
///     repo-blob listings, and GGUF header reads on every advisor refresh. A per-key gate serializes concurrent
///     misses for the same key so two callers racing on a cold entry issue one fetch, not two. Expiry is checked
///     lazily on read (no background sweep) — sized for the low cardinality of a single node's discovery traffic
///     (dozens to low hundreds of distinct repos/queries), so unbounded key growth is not a concern in practice.
/// </summary>
internal sealed class TtlCache<TValue>
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public TtlCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     Returns the cached value for <paramref name="key" /> when present and unexpired; otherwise invokes
    ///     <paramref name="factory" /> once, caches the result for <paramref name="ttl" />, and returns it. A
    ///     <paramref name="ttl" /> of zero or less disables caching for this call (always invokes the factory).
    /// </summary>
    public async Task<TValue> GetOrAddAsync(string key, TimeSpan ttl, Func<CancellationToken, Task<TValue>> factory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (ttl <= TimeSpan.Zero)
        {
            return await factory(ct).ConfigureAwait(false);
        }

        var entry = _entries.GetOrAdd(key, static _ => new CacheEntry());

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (entry.HasValue && entry.ExpiresAt > now)
            {
                return entry.Value;
            }

            var value = await factory(ct).ConfigureAwait(false);
            entry.Value = value;
            entry.ExpiresAt = now + ttl;
            entry.HasValue = true;
            return value;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class CacheEntry
    {
        public SemaphoreSlim Gate { get; } = new(initialCount: 1, maxCount: 1);
        public bool HasValue { get; set; }
        public TValue Value { get; set; } = default!;
        public DateTimeOffset ExpiresAt { get; set; }
    }
}
