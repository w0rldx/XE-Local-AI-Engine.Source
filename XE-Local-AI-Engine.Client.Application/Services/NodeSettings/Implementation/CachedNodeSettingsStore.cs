namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Caching.Memory;

/// <summary>
///     An <see cref="INodeSettingsStore" /> decorator that caches the loaded settings object in
///     <see cref="IMemoryCache" /> behind a single key.
/// </summary>
/// <remarks>
///     A single-entry, no-TTL cache: node settings are read often and change only via an operator <c>SaveAsync</c>, and the inner file store
///     keeps its semaphore + 0600-perms behaviour. A write only INVALIDATES the entry, never publishes its own value, and a LOAD publishes only
///     under a <c>_writeVersion</c> guard — both because a value published across a concurrent write would then be permanently stale.
///     Why each rule is the order-insensitive one: docs/wiki/08-data-and-persistence.md ("Reading: the cache, and the synchronous twins").
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
        var loaded = await _inner.LoadAsync(cancellationToken);
        PublishIfUnchanged(loaded, observedVersion);
        return loaded;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Forwarded WHOLE and deliberately NOT cached. Caching a <see langword="null" /> would make a transient read
    ///     failure sticky for the lifetime of a no-TTL entry, and the sole caller runs once per boot. The override itself
    ///     is load-bearing: without it this decorator inherits the interface default and answers from the TOLERANT
    ///     <see cref="LoadAsync" />, which cannot tell a missing file from an unreadable one — the whole point of the
    ///     strict read.
    /// </remarks>
    public Task<StoredNodeSettings?> LoadStrictAsync(CancellationToken cancellationToken = default)
    {
        return _inner.LoadStrictAsync(cancellationToken);
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

        await _inner.SaveAsync(settings, cancellationToken);
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
        var persisted = await _inner.UpdateAsync(mutate, cancellationToken);
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
