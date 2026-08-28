namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Caching.Memory;

/// <summary>
///     An <see cref="INodeSettingsStore" /> decorator that caches the loaded settings object in
///     <see cref="IMemoryCache" /> behind a single key. Node settings are read often and depended on widely but change
///     only via an operator <c>SaveAsync</c>, so a single-entry, no-TTL cache turns the common read into a sub-millisecond
///     in-memory hit. The inner file store keeps its semaphore + 0600-perms behavior; this decorator only adds caching.
/// </summary>
/// <remarks>
///     <para>
///         WHY a write only INVALIDATES and never publishes: this decorator cannot observe the order in which two
///         concurrent writes reached disk (they serialize inside the inner store, which reports no ordering), so a write
///         that published its own value could overwrite the cache with a version the next write had already superseded.
///         With a no-TTL cache, that stale entry is permanent — every reader, the reconciliation pass included, keeps
///         seeing settings that are no longer on disk. Dropping the entry is order-INSENSITIVE: whichever write clears
///         it last, the cache ends empty and the next read repopulates it from the canonical store.
///     </para>
///     <para>
///         WHY a LOAD's publication is version-guarded: a load's disk read can straddle a concurrent write, so
///         publishing its result unconditionally would reintroduce the same permanently-stale entry. Every write bumps
///         <c>_writeVersion</c> under the gate the load publishes under, so a load that overlapped one declines to
///         publish and merely costs the next reader a file read.
///     </para>
/// </remarks>
public sealed class CachedNodeSettingsStore : INodeSettingsStore
{
    private const string CacheKey = "node-settings:current";

    private readonly IMemoryCache _cache;
    private readonly INodeSettingsStore _inner;
    private readonly Lock _publishGate = new();

    // Bumped once per completed write, under _publishGate. A load carries the value it observed before its own read
    // and publishes only if it has not moved.
    private long _writeVersion;

    public CachedNodeSettingsStore(INodeSettingsStore inner, IMemoryCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out StoredNodeSettings? cached) && cached is not null)
        {
            return cached;
        }

        var observedVersion = ObserveWriteVersion();
        var loaded = await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
        PublishIfUnchanged(loaded, observedVersion);
        return loaded;
    }

    public StoredNodeSettings Load(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out StoredNodeSettings? cached) && cached is not null)
        {
            return cached;
        }

        var observedVersion = ObserveWriteVersion();
        var loaded = _inner.Load(cancellationToken);
        PublishIfUnchanged(loaded, observedVersion);
        return loaded;
    }

    public async Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _inner.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        Invalidate();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Delegated WHOLE to the inner store rather than composed here out of a cached load plus a save: the point of
    ///     the operation is that the read and the write happen under one lock, and reading from this cache would put the
    ///     mutation outside it — reintroducing exactly the lost-update window it exists to close.
    /// </remarks>
    public async Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
    {
        var persisted = await _inner.UpdateAsync(mutate, cancellationToken).ConfigureAwait(false);
        Invalidate();
        return persisted;
    }

    private long ObserveWriteVersion()
    {
        lock (_publishGate)
        {
            return _writeVersion;
        }
    }

    private void Invalidate()
    {
        lock (_publishGate)
        {
            _writeVersion++;
            _cache.Remove(CacheKey);
        }
    }

    private void PublishIfUnchanged(StoredNodeSettings settings, long observedVersion)
    {
        lock (_publishGate)
        {
            if (_writeVersion == observedVersion)
            {
                _cache.Set(CacheKey, settings);
            }
        }
    }
}
