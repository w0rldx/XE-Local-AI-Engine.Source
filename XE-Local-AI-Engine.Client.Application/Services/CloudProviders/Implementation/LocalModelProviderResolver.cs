namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="ILocalModelProviderResolver" />. Holds the registered provider set keyed by provider name
///     and reads the persisted per-model→provider map through a fresh DI scope per lookup (so a singleton router can
///     consume the scoped <see cref="ICoordinatedModelProviderMapStore" /> safely). Unmapped models route to the configured
///     default provider.
/// </summary>
/// <remarks>
///     The <c>ModelName → ProviderName</c> lookup is memoized in a short-TTL, bounded cache. Provider
///     resolution runs several times per chat turn (capability gating, model resolution, per-orchestration-participant),
///     each previously opening a fresh DI scope + coordinated map read; the map is effectively
///     write-once per model (a GGUF is always <c>llamacpp</c>, an Ollama model always <c>ollama</c>), so caching the name
///     for a few seconds collapses that to one read per turn. Only non-secret model/provider names are cached. Writers of
///     the map call <see cref="InvalidateModelProviderMap" /> after an upsert so a new row is visible immediately; the
///     TTL is the backstop for any unhooked writer.
/// </remarks>
public sealed class LocalModelProviderResolver : ILocalModelProviderResolver
{
    /// <summary>Default lifetime of a cached <c>ModelName → ProviderName</c> entry when the caller does not override it.</summary>
    private static readonly TimeSpan DefaultMapCacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>Defensive upper bound on cached entries; model counts are tiny, so hitting this only happens under an odd flood.</summary>
    private const int MaxCacheEntries = 512;

    private readonly TimeSpan _mapCacheTtl;
    private readonly ConcurrentDictionary<string, ProviderNameCacheEntry> _providerNameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    private readonly string _defaultProviderName;
    private readonly IReadOnlyDictionary<string, ILocalModelProvider> _providersByName;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    ///     Builds the resolver over every registered <see cref="ILocalModelProvider" /> (llama-server + the optional
    ///     Ollama provider), the scope factory used to read the per-model map, the configured default provider for
    ///     unmapped models, and the loaded-process cap surfaced to the preview cap check. The optional cache TTL /
    ///     time provider exist for deterministic tests; a non-positive TTL disables the map cache entirely.
    /// </summary>
    public LocalModelProviderResolver(IEnumerable<ILocalModelProvider> providers,
        IServiceScopeFactory scopeFactory,
        string defaultProviderName,
        int maxLoadedProcesses,
        TimeSpan? mapCacheTtl = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderName);
        _mapCacheTtl = mapCacheTtl ?? DefaultMapCacheTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // Last registration wins per key so a host can override a provider; provider keys are case-insensitive to match
        // LocalModelSelection routing across the persisted map and capability payloads.
        var byName = new Dictionary<string, ILocalModelProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }

            byName[provider.ProviderName] = provider;
        }

        if (byName.Count == 0)
        {
            throw new InvalidOperationException("No ILocalModelProvider is registered; cannot resolve a local model runtime.");
        }

        if (!byName.TryGetValue(defaultProviderName, out var defaultProvider))
        {
            throw new InvalidOperationException($"The configured default local model provider '{defaultProviderName}' is not registered.");
        }

        _providersByName = byName;
        _defaultProviderName = defaultProviderName;
        DefaultProvider = defaultProvider;
        MaxLoadedProcesses = maxLoadedProcesses;
    }

    /// <inheritdoc />
    public int MaxLoadedProcesses { get; }

    /// <inheritdoc />
    public ILocalModelProvider DefaultProvider { get; }

    /// <inheritdoc />
    public async Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        return await ResolveProviderNameCoreAsync(modelName, existingLease: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ResolveProviderNameForModelAsync(string modelName,
        IModelProviderMapReadLease existingLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingLease);
        return await ResolveProviderNameCoreAsync(modelName, existingLease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveProviderNameCoreAsync(string modelName,
        IModelProviderMapReadLease? existingLease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var cachingEnabled = _mapCacheTtl > TimeSpan.Zero;
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        if (existingLease is null
            && cachingEnabled
            && _providerNameCache.TryGetValue(modelName, out var cached)
            && cached.ExpiresAtTicks > nowTicks)
        {
            return cached.ProviderName;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
        ModelProviderMapReadLease? acquiredLease = null;
        IModelProviderMapReadLease lease;
        if (existingLease is null)
        {
            var leaseCoordinator = scope.ServiceProvider.GetRequiredService<IModelProviderMapLeaseCoordinator>();
            acquiredLease = await leaseCoordinator.AcquireMapReadAsync(modelName, cancellationToken).ConfigureAwait(false);
            lease = acquiredLease;
        }
        else
        {
            lease = existingLease;
        }

        try
        {
            var mapping = await mapStore.ReadWithRevisionAsync(lease, modelName, cancellationToken).ConfigureAwait(false);
            var mapped = mapping?.ProviderName;

            // An unmapped model routes to the configured default provider; a mapped row wins.
            var resolved = string.IsNullOrWhiteSpace(mapped) ? _defaultProviderName : mapped;

            if (cachingEnabled)
            {
                // Defensive bound: model names are few, but never let an odd flood of distinct names grow the cache unbounded.
                if (_providerNameCache.Count >= MaxCacheEntries)
                {
                    _providerNameCache.Clear();
                }

                _providerNameCache[modelName] = new ProviderNameCacheEntry(resolved, nowTicks + _mapCacheTtl.Ticks);
            }

            return resolved;
        }
        finally
        {
            if (acquiredLease is not null)
            {
                await acquiredLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public void InvalidateModelProviderMap()
    {
        _providerNameCache.Clear();
    }

    /// <inheritdoc />
    public ILocalModelProvider ResolveProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (_providersByName.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"No registered local model provider matches '{providerName}'.");
    }

    /// <inheritdoc />
    public async Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var providerName = await ResolveProviderNameForModelAsync(modelName, cancellationToken).ConfigureAwait(false);
        return ResolveProvider(providerName);
    }

    /// <summary>One cached <c>ModelName → ProviderName</c> resolution and the absolute UTC tick at which it expires.</summary>
    private readonly record struct ProviderNameCacheEntry(string ProviderName, long ExpiresAtTicks);
}
