namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     AUD4-16: the resolver memoizes <c>ModelName → ProviderName</c> in a short-TTL cache so the several per-turn
///     lookups collapse to one persisted read, and an explicit invalidation makes a freshly-written mapping visible
///     immediately rather than after the TTL.
/// </summary>
public sealed class LocalModelProviderResolverCacheTests
{
    [Test]
    public async Task ResolveProviderNameForModelAsync_RepeatedWithinTtl_ReadsTheMapOnce()
    {
        var store = new CountingMapStore();
        var resolver = CreateResolver(store, TimeSpan.FromMinutes(1));

        var first = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");
        var second = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");
        var third = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");

        AssertEx.Equal("llamacpp", first);
        AssertEx.Equal("llamacpp", second);
        AssertEx.Equal("llamacpp", third);
        // Three logical resolutions (as a turn would issue), a single persisted read.
        AssertEx.Equal(expected: 1, store.ReadCount);
    }

    [Test]
    public async Task InvalidateModelProviderMap_ForcesTheNextLookupToReReadTheMap()
    {
        var store = new CountingMapStore();
        var resolver = CreateResolver(store, TimeSpan.FromMinutes(1));

        _ = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");
        resolver.InvalidateModelProviderMap();
        _ = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");

        AssertEx.Equal(expected: 2, store.ReadCount);
    }

    [Test]
    public async Task ResolveProviderNameForModelAsync_WhenTtlIsNonPositive_AlwaysReadsTheMap()
    {
        var store = new CountingMapStore();
        var resolver = CreateResolver(store, TimeSpan.Zero);

        _ = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");
        _ = await resolver.ResolveProviderNameForModelAsync("qwen3:8b");

        AssertEx.Equal(expected: 2, store.ReadCount);
    }

    private static LocalModelProviderResolver CreateResolver(IModelProviderMapStore store, TimeSpan ttl)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("llamacpp");

        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new LocalModelProviderResolver([provider], scopeFactory, "llamacpp", maxLoadedProcesses: 3, mapCacheTtl: ttl);
    }

    private sealed class CountingMapStore : IModelProviderMapStore
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<string?> GetProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _readCount);
            // Return null so the resolver falls back to the configured default (llamacpp) — the read still counts.
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ModelProviderMapRecord>>([]);
        }

        public Task<ModelProviderMapRecord> UpsertAsync(string modelName, string providerName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelProviderMapRecord(modelName, providerName, UpdatedAtUtc: 0));
        }
    }
}
