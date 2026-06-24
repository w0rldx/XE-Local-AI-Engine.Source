namespace XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;

using Microsoft.Extensions.Caching.Memory;

/// <summary>
///     An <see cref="INodeSettingsStore" /> decorator that caches the loaded settings object in
///     <see cref="IMemoryCache" /> behind a single key. Node settings are read often and depended on widely but change
///     only via an operator <c>SaveAsync</c>, so a single-entry, no-TTL cache turns the common read into a sub-millisecond
///     in-memory hit. The inner file store keeps its semaphore + 0600-perms behavior; this decorator only adds caching:
///     <see cref="LoadAsync" /> populates the entry on a miss and <see cref="SaveAsync" /> re-primes it with the value the
///     inner store persisted (its <c>Normalize</c> output), keeping the cache coherent with disk.
/// </summary>
public sealed class CachedNodeSettingsStore : INodeSettingsStore
{
    private const string CacheKey = "node-settings:current";

    private readonly IMemoryCache _cache;
    private readonly INodeSettingsStore _inner;

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

        var loaded = await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
        _cache.Set(CacheKey, loaded);
        return loaded;
    }

    public StoredNodeSettings Load(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out StoredNodeSettings? cached) && cached is not null)
        {
            return cached;
        }

        var loaded = _inner.Load(cancellationToken);
        _cache.Set(CacheKey, loaded);
        return loaded;
    }

    public async Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _inner.SaveAsync(settings, cancellationToken).ConfigureAwait(false);

        // Invalidate, then re-prime from the canonical inner store so a subsequent read reflects the persisted
        // (normalized) shape rather than the unnormalized request object.
        _cache.Remove(CacheKey);
        var persisted = await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
        _cache.Set(CacheKey, persisted);
    }
}
