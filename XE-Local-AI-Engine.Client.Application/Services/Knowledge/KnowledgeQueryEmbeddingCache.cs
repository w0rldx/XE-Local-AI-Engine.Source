namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Default <see cref="IKnowledgeQueryEmbeddingCache" />. A bounded, TTL'd, approximately-LRU cache following the same
///     shape as the Hugging Face <c>TtlCache</c>: a <see cref="ConcurrentDictionary{TKey,TValue}" /> with a monotonic
///     access stamp for recency, lazy expiry on read, and an O(n) coldest-first eviction scan that runs only on the
///     insert path while over capacity. The key is the resolved model's policy-family identity plus a SHA-256 hash of the
///     query, so the raw query text is never retained. Each value carries its exact canonical identity and vector for
///     caller-side policy/width validation. Registered as a singleton so one cache serves every request.
/// </summary>
public sealed class KnowledgeQueryEmbeddingCache : IKnowledgeQueryEmbeddingCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly int _maxEntries;
    private readonly TimeSpan _ttl;
    private long _accessClock;
    private int _evicting;

    public KnowledgeQueryEmbeddingCache(IOptions<KnowledgeBaseOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxEntries = Math.Max(1, options.Value.QueryEmbeddingCacheMaxEntries);
        _ttl = TimeSpan.FromSeconds(Math.Max(0, options.Value.QueryEmbeddingCacheTtlSeconds));
    }

    public bool TryGet(string policyFamilyIdentity, string query, out KnowledgeQueryEmbeddingCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFamilyIdentity);
        ArgumentNullException.ThrowIfNull(query);

        if (_ttl > TimeSpan.Zero
            && _entries.TryGetValue(BuildKey(policyFamilyIdentity, query), out var cached)
            && cached.ExpiresAt > _timeProvider.GetUtcNow())
        {
            Touch(cached);
            entry = cached.Value;
            RecordLookup(hit: true);
            return true;
        }

        entry = default!;
        RecordLookup(hit: false);
        return false;
    }

    public void Store(string policyFamilyIdentity, string query, KnowledgeQueryEmbeddingCacheEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyFamilyIdentity);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.VectorIdentity))
        {
            throw new ArgumentException("The cached vector identity is required.", nameof(entry));
        }

        if (_ttl <= TimeSpan.Zero || entry.Vector.IsEmpty)
        {
            return;
        }

        var key = BuildKey(policyFamilyIdentity, query);
        var cached = new Entry(entry, _timeProvider.GetUtcNow() + _ttl);
        Touch(cached);
        _entries[key] = cached;
        EvictIfOverCapacity(key);
    }

    private static string BuildKey(string policyFamilyIdentity, string query)
    {
        // Hash the query so the raw (potentially sensitive) text is never retained as a dictionary key. The policy-family
        // identity is not sensitive and stays in the clear so model/policy changes yield distinct key spaces.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(query));
        return string.Concat(policyFamilyIdentity, "\n", Convert.ToHexStringLower(hash));
    }

    private static void RecordLookup(bool hit)
    {
        NodeMetrics.KnowledgeQueryEmbeddingCacheLookupsTotal.Add(1,
            new KeyValuePair<string, object?>("result", hit ? "hit" : "miss"));
    }

    private void Touch(Entry entry)
    {
        Volatile.Write(ref entry.LastAccessStamp, Interlocked.Increment(ref _accessClock));
    }

    private void EvictIfOverCapacity(string justInsertedKey)
    {
        if (_entries.Count <= _maxEntries)
        {
            return;
        }

        // Serialize eviction so a burst of inserts doesn't each run the O(n) scan; a caller that finds one in progress
        // relies on the next over-capacity insert to retry.
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();

            // Pass 1: drop expired entries (cheapest to lose), never the just-inserted key.
            foreach (var pair in _entries)
            {
                if (_entries.Count <= _maxEntries)
                {
                    return;
                }

                if (pair.Key != justInsertedKey && pair.Value.ExpiresAt <= now)
                {
                    _entries.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value));
                }
            }

            if (_entries.Count <= _maxEntries)
            {
                return;
            }

            // Pass 2: evict the coldest live entries by access stamp until back at/below capacity.
            var coldest = _entries
                          .Where(pair => pair.Key != justInsertedKey)
                          .OrderBy(pair => Volatile.Read(ref pair.Value.LastAccessStamp))
                          .ToList();

            foreach (var pair in coldest)
            {
                if (_entries.Count <= _maxEntries)
                {
                    return;
                }

                _entries.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value));
            }
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }

    private sealed class Entry(KnowledgeQueryEmbeddingCacheEntry value, DateTimeOffset expiresAt)
    {
        public long LastAccessStamp;
        public KnowledgeQueryEmbeddingCacheEntry Value { get; } = value;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
    }
}
