namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the post-flip routing default: an UNMAPPED model routes to <c>llamacpp</c> (the shipped default model is a
///     GGUF), while a model explicitly mapped to <c>ollama</c> still routes to Ollama. This is the routing half of the
///     "Ollama is now optional" decision — the flipped default only governs truly-unmapped names.
/// </summary>
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
    public async Task ModelMappedToOllama_StillRoutesToOllama()
    {
        var resolver = BuildResolver(new Dictionary<string, string>
        {
            ["llama3.2:3b"] = OllamaLocalModelProvider.OllamaProviderName
        });

        var provider = await resolver.ResolveProviderNameForModelAsync("llama3.2:3b");

        AssertEx.Equal(OllamaLocalModelProvider.OllamaProviderName, provider);
    }

    private static LocalModelProviderResolver BuildResolver(IReadOnlyDictionary<string, string> mappings)
    {
        var services = new ServiceCollection();
        services.AddScoped<IModelProviderMapStore>(_ => new FakeModelProviderMapStore(mappings));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // Both runtimes registered; the flipped default = llamacpp (mirrors AddNodeModelRuntimeExtensions post-flip).
        ILocalModelProvider[] providers =
        [
            new StubProvider(LlamaServerProviderConstants.ProviderName),
            new StubProvider(OllamaLocalModelProvider.OllamaProviderName)
        ];

        return new LocalModelProviderResolver(providers, scopeFactory, LlamaServerProviderConstants.ProviderName, maxLoadedProcesses: 3);
    }

    private sealed class FakeModelProviderMapStore(IReadOnlyDictionary<string, string> mappings) : IModelProviderMapStore
    {
        private readonly Dictionary<string, string> _mappings = new(mappings, StringComparer.OrdinalIgnoreCase);

        public Task<string?> GetProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_mappings.TryGetValue(modelName, out var provider) ? provider : null);
        }

        public Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ModelProviderMapRecord>>(_mappings.Select(pair => new ModelProviderMapRecord(pair.Key, pair.Value, UpdatedAtUtc: 0)).ToArray());
        }

        public Task<ModelProviderMapRecord> UpsertAsync(string modelName, string providerName, CancellationToken cancellationToken = default)
        {
            _mappings[modelName] = providerName;
            return Task.FromResult(new ModelProviderMapRecord(modelName, providerName, UpdatedAtUtc: 0));
        }
    }

    /// <summary>A minimal provider used only for its <see cref="ILocalModelProvider.ProviderName" /> key.</summary>
    private sealed class StubProvider(string providerName) : ILocalModelProvider
    {
        public string ProviderName => providerName;

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
