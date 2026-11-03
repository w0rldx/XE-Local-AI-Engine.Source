namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the routing default: an UNMAPPED model routes to <c>llamacpp</c> (the shipped default model is a GGUF),
///     while a mapped model routes to the provider its row names. The default governs only truly-unmapped names.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelProviderResolverDefaultTests
{
    [Test]
    public async Task UnmappedModel_RoutesToLlamaCpp_UnderFlippedDefault()
    {
        var resolver = BuildResolver(new Dictionary<string, string>());

        var provider = await resolver.ResolveProviderNameForModelAsync("some-unmapped-gguf:Q4_K_M");

        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, provider);
    }

    [Test]
    public async Task UnmappedExternalModel_RoutesToTheExternalProvider_NotTheDefault()
    {
        // A dangling ext: pin (its connection was deleted) must fail with the external provider's "no longer registered"
        // text; routed to the default runtime it failed as "The requested model is not installed."
        var resolver = BuildResolver(new Dictionary<string, string>());

        var provider = await resolver.ResolveProviderNameForModelAsync("ext:deleted-box/some-model");

        AssertEx.Equal(ExternalProviderConstants.ProviderName, provider);
    }

    [Test]
    public async Task ModelWhoseOldRuntimeRowIsGone_RoutesToLlamaCpp_AndFailsAsNotInstalled()
    {
        // The migration deletes old map rows, so a saved agent still naming such a model is simply unmapped: it reaches
        // the real llama.cpp provider, whose store has no such file, and gets the ordinary not-installed failure.
        var store = new FakeModelStore(fixedPath: null);
        await using var supervisor = SupervisorFactory.Create(modelStore: store);
        var resolver = SingleProviderResolverFactory.Create(new LlamaServerLocalModelProvider(supervisor, store, TimeProvider.System));

        var providerName = await resolver.ResolveProviderNameForModelAsync("llama3.2:3b");
        var provider = await resolver.ResolveProviderForModelAsync("llama3.2:3b");
        var failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => provider.WarmModelAsync("llama3.2:3b", CancellationToken.None));

        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, providerName);
        AssertEx.Contains(failure.Message, "not installed", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task MappedModel_RoutesToItsMappedProvider_NotTheDefault()
    {
        var resolver = BuildResolver(new Dictionary<string, string>
        {
            ["registered-model"] = ExternalProviderConstants.ProviderName
        });

        var provider = await resolver.ResolveProviderNameForModelAsync("registered-model");

        AssertEx.Equal(ExternalProviderConstants.ProviderName, provider);
    }

    private static LocalModelProviderResolver BuildResolver(IReadOnlyDictionary<string, string> mappings)
    {
        var services = new ServiceCollection();
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        foreach (var mapping in mappings)
        {
            mapStore.Seed(mapping.Key, mapping.Value);
        }

        services.AddSingleton<IModelProviderMapLeaseCoordinator>(new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain()));
        services.AddScoped<ICoordinatedModelProviderMapStore>(_ => mapStore);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // The default = llamacpp, as AddNodeModelRuntimeExtensions registers it.
        ILocalModelProvider[] providers =
        [
            new StubProvider(LlamaServerProviderConstants.ProviderName),
            new StubProvider(ExternalProviderConstants.ProviderName)
        ];

        return new LocalModelProviderResolver(providers, scopeFactory, LlamaServerProviderConstants.ProviderName, maxLoadedProcesses: 3, timeProvider: TimeProvider.System);
    }

    /// <summary>A minimal provider used only for its <see cref="ILocalModelProvider.ProviderName" /> key.</summary>
    private sealed class StubProvider : ILocalModelProvider
    {
        private readonly string _providerName;

        public StubProvider(string providerName)
        {
            _providerName = providerName;
        }

        public string ProviderName => _providerName;

        public IChatClient CreateChatClient(LocalModelSelection selection)
        {
            throw new NotSupportedException();
        }

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            throw new NotSupportedException();
        }

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task WarmModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task UnloadModelAsync(string modelName, CancellationToken ct)
        {
            throw new NotSupportedException();
        }
    }
}
