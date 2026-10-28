namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Wraps one <see cref="ILocalModelProvider" /> in the real <see cref="LocalModelProviderResolver" /> for consumer
///     unit tests: it is the only registered provider AND the default with an empty per-model map, so every model
///     routes to it and any other provider name fails to resolve. No Ollama, no network.
/// </summary>
internal static class SingleProviderResolverFactory
{
    public static ILocalModelProviderResolver Create(ILocalModelProvider provider, int maxLoadedProcesses = 8)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var services = new ServiceCollection();
        services.AddSingleton<IModelProviderMapLeaseCoordinator>(new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()));
        services.AddScoped<ICoordinatedModelProviderMapStore>(_ => new InMemoryCoordinatedModelProviderMapStore());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new LocalModelProviderResolver([provider], scopeFactory, provider.ProviderName, maxLoadedProcesses, TimeProvider.System);
    }
}
