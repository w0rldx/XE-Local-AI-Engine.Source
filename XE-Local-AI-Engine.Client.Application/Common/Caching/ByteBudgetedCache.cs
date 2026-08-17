namespace XE_Local_AI_Engine.Client.Common.Caching;

using System.Collections.Concurrent;

/// <summary>
///     The one RAM-only vector cache behind the node's three embedding-reuse sites (playbook-retrieval ranking, semantic
///     memory dedup, knowledge-search query embeddings). Each of those hand-rolled its own count-bounded, insertion-order
///     ("oldest inserted wins the eviction") dictionary, which evicts a hot entry while a cold one survives and bounds
///     nothing in RAM terms — 512 entries is 6 MB at 768 dimensions and 33 MB at 4096.
///     What this adds over those three: eviction ordered by last access (LRU) rather than insertion; a byte budget
///     alongside the entry bound, so a wide-vector model cannot silently multiply the cache's footprint; optional TTL
///     expiry; and in-flight coalescing, so concurrent callers missing on the same key wait for one computation instead of
///     each paying a round-trip to a single-slot (<c>--parallel 1</c>) embedding server.
///     Nothing here is persisted, logged, or returned outside the process; keys are the callers' concern (the query cache
///     hashes its query text before it ever reaches this type).
/// </summary>
/// <typeparam name="TKey">Cache key. Callers pick a key that already encodes the invalidation inputs (id, version, model).</typeparam>
/// <typeparam name="TValue">Cached value — an embedding vector, or a small record wrapping one.</typeparam>
public sealed class ByteBudgetedCache<TKey, TValue>
    where TKey : notnull
{
    private readonly Func<TKey, TValue, long> _costInBytes;
    private readonly ConcurrentDictionary<TKey, Entry> _entries;
    private readonly ConcurrentDictionary<TKey, TaskCompletionSource<Resolution>> _inFlight;
    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private readonly Action<long>? _onEvictedBytes;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeToLive;
    private long _accessClock;
    private int _evicting;
    private long _sizeInBytes;

    /// <param name="maxBytes">Byte budget across all live entries. Floored at 1.</param>
    /// <param name="maxEntries">Entry bound, applied alongside the byte budget (whichever bites first). Floored at 1.</param>
    /// <param name="costInBytes">
    ///     Approximate retained size of one entry (key plus value). An approximation is the point — this bounds RAM, it
    ///     does not measure it.
    /// </param>
    /// <param name="timeToLive">Entry lifetime; <see cref="TimeSpan.Zero" /> (the default) means entries never expire.</param>
    /// <param name="timeProvider">Clock, for TTL. Defaults to <see cref="TimeProvider.System" />.</param>
    /// <param name="onEvictedBytes">Invoked with the bytes reclaimed by an eviction pass, for callers that meter it.</param>
    /// <param name="keyComparer">Key comparer; defaults to <typeparamref name="TKey" />'s own equality.</param>
    public ByteBudgetedCache(long maxBytes,
        int maxEntries,
        Func<TKey, TValue, long> costInBytes,
        TimeSpan timeToLive = default,
        TimeProvider? timeProvider = null,
        Action<long>? onEvictedBytes = null,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(costInBytes);

        _maxBytes = Math.Max(1, maxBytes);
        _maxEntries = Math.Max(1, maxEntries);
        _costInBytes = costInBytes;
        _timeToLive = timeToLive;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onEvictedBytes = onEvictedBytes;
        _entries = new ConcurrentDictionary<TKey, Entry>(keyComparer);
        _inFlight = new ConcurrentDictionary<TKey, TaskCompletionSource<Resolution>>(keyComparer);
    }

    /// <summary>Live entry count (expired-but-unswept entries included).</summary>
    public int Count => _entries.Count;

    /// <summary>
    ///     Approximate retained bytes. Resynchronized exactly on every eviction pass, so concurrent-insert drift is
    ///     bounded to one eviction cycle rather than accumulating.
    /// </summary>
    public long ApproximateSizeInBytes => Interlocked.Read(ref _sizeInBytes);

    /// <summary>Returns a live (non-expired) value and marks it as most-recently used.</summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && !IsExpired(entry, _timeProvider.GetUtcNow()))
        {
            Touch(entry);
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Inserts or replaces an entry, then evicts coldest-first until both bounds hold again.</summary>
    public void Set(TKey key, TValue value)
    {
        var entry = new Entry(value, _costInBytes(key, value), _timeToLive > TimeSpan.Zero ? _timeProvider.GetUtcNow() + _timeToLive : null);
        Touch(entry);

        var previousCost = _entries.TryGetValue(key, out var previous) ? previous.CostInBytes : 0;
        _entries[key] = entry;
        Interlocked.Add(ref _sizeInBytes, entry.CostInBytes - previousCost);

        EvictWhileOverBudget(key);
    }

    /// <summary>
    ///     Resolves a whole batch of keys in one pass: cached keys come back directly, keys another caller is already
    ///     computing are awaited rather than recomputed, and everything left over is handed to
    ///     <paramref name="computeMissing" /> as a single list so the callers that batch their embedding round-trip keep
    ///     doing exactly one.
    ///     <paramref name="computeMissing" /> is invoked ONCE per call even when nothing is missing — all three call sites
    ///     have uncacheable work to fold into the same round-trip (the search query, the extraction candidates), and
    ///     skipping the invocation would cost them a second round-trip against a single-slot server.
    /// </summary>
    /// <param name="keys">Keys to resolve; the returned array is index-aligned with this list.</param>
    /// <param name="computeMissing">
    ///     Computes one value per key it is handed, in order. Returning <see langword="null" /> (or a wrong-length list)
    ///     signals a degrade: no values are cached and this method returns <see langword="null" />. Exceptions propagate
    ///     to this caller only — never to a coalesced waiter, whose own <c>catch</c> filters were written for its own
    ///     failure modes.
    /// </param>
    /// <param name="cancellationToken">Cancels both the computation and any wait on another caller's computation.</param>
    /// <returns>One value per key, or <see langword="null" /> if the batch could not be fully resolved.</returns>
    public async Task<TValue[]?> GetOrAddManyAsync(IReadOnlyList<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyList<TValue>?>> computeMissing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(computeMissing);

        var values = new TValue[keys.Count];
        var claimed = new List<Claim>();
        List<PendingWait>? waits = null;

        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            if (TryGet(key, out var cached))
            {
                values[index] = cached;
                continue;
            }

            // First caller to publish a completion source for this key owns computing it; everyone else awaits that one.
            // A key repeated inside this very batch also lands in `waits` and is satisfied below, since claims are
            // completed before any wait is awaited.
            var claim = new TaskCompletionSource<Resolution>(TaskCreationOptions.RunContinuationsAsynchronously);
            var owner = _inFlight.GetOrAdd(key, claim);
            if (ReferenceEquals(owner, claim))
            {
                claimed.Add(new Claim(key, index, claim));
            }
            else
            {
                (waits ??= []).Add(new PendingWait(index, owner.Task));
            }
        }

        var missing = claimed.Select(static claim => claim.Key).ToArray();
        IReadOnlyList<TValue>? computed;
        try
        {
            computed = await computeMissing(missing, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseClaims(claimed);
            throw;
        }

        if (computed is null || computed.Count != missing.Length)
        {
            ReleaseClaims(claimed);
            return null;
        }

        for (var position = 0; position < claimed.Count; position++)
        {
            var claim = claimed[position];
            var value = computed[position];
            values[claim.Index] = value;
            Set(claim.Key, value);
            _inFlight.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<Resolution>>(claim.Key, claim.Completion));
            claim.Completion.TrySetResult(new Resolution(Resolved: true, value));
        }

        if (waits is null)
        {
            return values;
        }

        foreach (var (index, wait) in waits)
        {
            var resolution = await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!resolution.Resolved)
            {
                // The caller that owned this key degraded; so does this batch, taking its own existing degrade path
                // rather than inheriting an exception type it was never written to catch.
                return null;
            }

            values[index] = resolution.Value;
        }

        return values;
    }

    // Completes claims as unresolved so coalesced waiters degrade instead of hanging, and clears them from the in-flight
    // map so the next caller re-computes rather than adopting a dead claim.
    private void ReleaseClaims(List<Claim> claimed)
    {
        foreach (var claim in claimed)
        {
            _inFlight.TryRemove(new KeyValuePair<TKey, TaskCompletionSource<Resolution>>(claim.Key, claim.Completion));
            claim.Completion.TrySetResult(default);
        }
    }

    private bool IsExpired(Entry entry, DateTimeOffset now)
    {
        return _timeToLive > TimeSpan.Zero && entry.ExpiresAt is { } expiresAt && expiresAt <= now;
    }

    private void Touch(Entry entry)
    {
        Volatile.Write(ref entry.LastAccessStamp, Interlocked.Increment(ref _accessClock));
    }

    private void EvictWhileOverBudget(TKey justInsertedKey)
    {
        if (Interlocked.Read(ref _sizeInBytes) <= _maxBytes && _entries.Count <= _maxEntries)
        {
            return;
        }

        // Serialize eviction so a burst of inserts does not each run the O(n) scan; a caller that finds one in progress
        // relies on the next over-budget insert to retry.
        if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var comparer = _entries.Comparer;
            var live = new List<KeyValuePair<TKey, Entry>>(_entries.Count);
            var totalBytes = 0L;
            var evictedBytes = 0L;

            // Pass 1: sweep expired entries (cheapest to lose) and recount the survivors exactly.
            foreach (var pair in _entries)
            {
                if (!comparer.Equals(pair.Key, justInsertedKey) && IsExpired(pair.Value, now))
                {
                    if (_entries.TryRemove(pair))
                    {
                        evictedBytes += pair.Value.CostInBytes;
                    }

                    continue;
                }

                live.Add(pair);
                totalBytes += pair.Value.CostInBytes;
            }

            // Pass 2: evict the coldest survivors — by last ACCESS, so a hot entry outlives a stale one regardless of
            // insertion order — until both bounds hold. The just-inserted key is never a victim of its own insert.
            var liveCount = live.Count;
            if (totalBytes > _maxBytes || liveCount > _maxEntries)
            {
                foreach (var pair in live.Where(pair => !comparer.Equals(pair.Key, justInsertedKey))
                                         .OrderBy(pair => Volatile.Read(ref pair.Value.LastAccessStamp)))
                {
                    if (totalBytes <= _maxBytes && liveCount <= _maxEntries)
                    {
                        break;
                    }

                    if (_entries.TryRemove(pair))
                    {
                        totalBytes -= pair.Value.CostInBytes;
                        evictedBytes += pair.Value.CostInBytes;
                        liveCount--;
                    }
                }
            }

            // Resync the counter from the live scan. An insert that landed mid-scan is dropped from the total, so the
            // budget can be briefly under-counted — self-corrected by the next eviction pass.
            Interlocked.Exchange(ref _sizeInBytes, totalBytes);

            if (evictedBytes > 0)
            {
                _onEvictedBytes?.Invoke(evictedBytes);
            }
        }
        finally
        {
            Volatile.Write(ref _evicting, 0);
        }
    }

    private sealed class Entry(TValue value, long costInBytes, DateTimeOffset? expiresAt)
    {
        public long LastAccessStamp;
        public TValue Value { get; } = value;
        public long CostInBytes { get; } = costInBytes;
        public DateTimeOffset? ExpiresAt { get; } = expiresAt;
    }

    private readonly record struct Resolution(bool Resolved, TValue Value);

    private readonly record struct Claim(TKey Key, int Index, TaskCompletionSource<Resolution> Completion);

    /// <summary>A batch slot whose value is being computed by another caller's in-flight claim.</summary>
    private readonly record struct PendingWait(int Index, Task<Resolution> Wait);
}
