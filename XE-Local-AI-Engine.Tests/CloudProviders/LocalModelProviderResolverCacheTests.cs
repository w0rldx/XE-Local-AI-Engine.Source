namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The resolver memoizes <c>ModelName → ProviderName</c> in a short-TTL cache so the several per-turn
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

    private static LocalModelProviderResolver CreateResolver(CountingMapStore store, TimeSpan ttl)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns("llamacpp");

        var services = new ServiceCollection();
        services.AddSingleton<IModelProviderMapLeaseCoordinator>(new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()));
        services.AddScoped<ICoordinatedModelProviderMapStore>(_ => store);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new LocalModelProviderResolver([provider], scopeFactory, "llamacpp", maxLoadedProcesses: 3, mapCacheTtl: ttl);
    }

    private sealed class CountingMapStore : ICoordinatedModelProviderMapStore
    {
        // Not a reconciliation fixture: only the external-provider pass enumerates the whole map, and this double
        // exists to drive a single model's leased path.
        public Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test double does not enumerate the provider map.");

        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _readCount);
            return Task.FromResult<ModelProviderMapRecord?>(null);
        }

        public Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease, string modelName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease, string modelName, string providerName, string? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease, ProviderMapMutationReceipt receipt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease, string modelName, string expectedProvider, string expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
