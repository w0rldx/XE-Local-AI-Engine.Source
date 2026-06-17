namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Wraps a single <see cref="ILocalModelProvider" /> in the real <see cref="LocalModelProviderResolver" /> for unit
///     tests of consumers that were re-pointed from a bare provider to the resolver (Lane A T5). The supplied provider
///     is the only registered provider AND the default, and the per-model map is empty, so every model routes to it and
///     any other provider name fails to resolve (the unregistered-provider degrade path). No Ollama / network.
/// </summary>
internal static class SingleProviderResolverFactory
{
    public static ILocalModelProviderResolver Create(ILocalModelProvider provider, int maxLoadedProcesses = 8)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var services = new ServiceCollection();
        services.AddScoped<IModelProviderMapStore>(_ => new EmptyModelProviderMapStore());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new LocalModelProviderResolver([provider], scopeFactory, provider.ProviderName, maxLoadedProcesses);
    }

    private sealed class EmptyModelProviderMapStore : IModelProviderMapStore
    {
        public Task<string?> GetProviderForModelAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelProviderMapRecord>>([]);

        public Task<ModelProviderMapRecord> UpsertAsync(string modelName, string providerName, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModelProviderMapRecord(modelName, providerName, 0));
    }
}
