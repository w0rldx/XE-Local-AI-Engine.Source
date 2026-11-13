namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The model-routing client routes each send by <see cref="ChatOptions.ModelId" /> to the mapped provider (or the
///     default), reaches a NEW per-model client when the model switches mid-session, caches one client per (provider,
///     model), and never disposes a resolved adapter.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ModelRoutingLocalChatClientTests
{
    private const string DefaultModel = "default-chat";

    private const string LlamaProvider = "llamacpp";
    private const string ExternalProvider = "external";

    private static ChatMessage[] Message => [new(ChatRole.User, "hi")];

    [Test]
    public async Task GetResponse_RoutesByModelId_ToMappedProviderAndDistinctPerModelClient()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var external = new RecordingLocalModelProvider(ExternalProvider);
        // "remote-model" is mapped to the external provider; "gguf-model" has no row → routes to the default provider (llamacpp).
        var resolver = BuildResolver([llamacpp, external],
            LlamaProvider,
            new Dictionary<string, string>
            {
                ["remote-model"] = ExternalProvider
            });
        using var router = new ModelRoutingLocalChatClient(resolver, StoredDefault(DefaultModel));

        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "gguf-model"
        });
        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "remote-model"
        });

        // Each model id reached the provider the map (or default) names, with the model carried through the selection.
        AssertEx.Equal(expected: 1, llamacpp.CreatedClients.Count);
        AssertEx.Equal("gguf-model", llamacpp.CreatedClients[0].ModelName);
        AssertEx.Equal(expected: 1, external.CreatedClients.Count);
        AssertEx.Equal("remote-model", external.CreatedClients[0].ModelName);

        // The two sends reached two DISTINCT per-model clients (distinct processes) — a model switch mid-session does
        // not reuse the previous model's client.
        AssertEx.False(ReferenceEquals(llamacpp.CreatedClients[0], external.CreatedClients[0]),
            "Two different models must resolve to two distinct per-model clients.");
    }

    [Test]
    public async Task GetResponse_WhenSameModelSentTwice_ReusesOneCachedClientPerProviderAndModel()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var resolver = BuildResolver([llamacpp], LlamaProvider, new Dictionary<string, string>());
        using var router = new ModelRoutingLocalChatClient(resolver, StoredDefault(DefaultModel));

        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "chat-a"
        });
        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "chat-a"
        });

        // One CreateChatClient for two sends of the same model — the (provider, model) cache held.
        AssertEx.Equal(expected: 1, llamacpp.CreatedClients.Count);
        AssertEx.Equal(expected: 2, llamacpp.CreatedClients[0].CallCount);
        AssertEx.False(llamacpp.CreatedClients[0].IsDisposed, "A cached client must not be disposed between sends.");
    }

    [Test]
    public async Task GetResponse_WhenModelIdOmitted_FallsBackToDefaultModel()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var resolver = BuildResolver([llamacpp], LlamaProvider, new Dictionary<string, string>());
        using var router = new ModelRoutingLocalChatClient(resolver, StoredDefault(DefaultModel));

        await router.GetResponseAsync(Message);

        AssertEx.Equal(expected: 1, llamacpp.CreatedClients.Count);
        AssertEx.Equal(DefaultModel, llamacpp.CreatedClients[0].ModelName);
    }

    [Test]
    public async Task GetResponse_WhenTheStoredDefaultChangesAfterConstruction_FallsBackToTheNewDefault()
    {
        // The node rewrites its own default after boot (first-run provisioning, the ext: reconciler, "Set default"), so a
        // request with no model must reach the default stored NOW, not the one this singleton was built with.
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var resolver = BuildResolver([llamacpp], LlamaProvider, new Dictionary<string, string>());
        var stored = new StoredNodeSettings
        {
            DefaultModelName = DefaultModel
        };
        using var router = new ModelRoutingLocalChatClient(resolver, SeededNodeRuntimeSettings.Create(stored: () => stored));

        await router.GetResponseAsync(Message);
        stored = stored with
        {
            DefaultModelName = "provisioned-chat"
        };
        await router.GetResponseAsync(Message);

        AssertEx.Equal(DefaultModel + ",provisioned-chat", string.Join(',', llamacpp.CreatedClients.Select(client => client.ModelName)));
    }

    [Test]
    public async Task GetResponse_WhenRequiredModelWasRemappedAwayFromLlama_RefusesBeforeClientSelection()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var external = new RecordingLocalModelProvider("external");
        var resolver = BuildResolver([llamacpp, external],
            LlamaProvider,
            new Dictionary<string, string>
            {
                ["gguf-model"] = "external"
            });
        using var router = new ModelRoutingLocalChatClient(resolver, StoredDefault(DefaultModel));
        using var scope = NodeManagedLlamaRoutingScope.Begin("gguf-model");

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "gguf-model"
        }));

        AssertEx.Empty(llamacpp.CreatedClients);
        AssertEx.Empty(external.CreatedClients);
    }

    [Test]
    public async Task GetStreamingResponse_WhenRequiredModelWasRemappedAwayFromLlama_RefusesBeforeClientSelection()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var external = new RecordingLocalModelProvider("external");
        var resolver = BuildResolver([llamacpp, external],
            LlamaProvider,
            new Dictionary<string, string>
            {
                ["gguf-model"] = "external"
            });
        using var router = new ModelRoutingLocalChatClient(resolver, StoredDefault(DefaultModel));
        using var scope = NodeManagedLlamaRoutingScope.Begin("gguf-model");

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in router.GetStreamingResponseAsync(Message, new ChatOptions
                           {
                               ModelId = "gguf-model"
                           }))
            {
                AssertEx.True(false, "A remapped node-managed model must be rejected before streaming starts.");
            }
        });

        AssertEx.Empty(llamacpp.CreatedClients);
        AssertEx.Empty(external.CreatedClients);
    }

    [Test]
    public async Task Dispose_DisposesCachedClients_ButNeverDuringASend()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var resolver = BuildResolver([llamacpp], LlamaProvider, new Dictionary<string, string>());
        var router = new ModelRoutingLocalChatClient(resolver, StoredDefault(DefaultModel));

        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "chat-a"
        });
        var resolvedDuringSend = llamacpp.CreatedClients[0];
        AssertEx.False(resolvedDuringSend.IsDisposed, "The resolved adapter must survive the send (router never disposes per-call).");

        router.Dispose();

        AssertEx.True(resolvedDuringSend.IsDisposed, "Disposing the router disposes the clients it cached.");
    }

    // The real stored-else-seed default resolution over a fixed stored default.
    private static INodeRuntimeSettings StoredDefault(string modelName) =>
        SeededNodeRuntimeSettings.Create(stored: () => new StoredNodeSettings
        {
            DefaultModelName = modelName
        });

    private static ILocalModelProviderResolver BuildResolver(IEnumerable<ILocalModelProvider> providers,
        string defaultProviderName,
        IReadOnlyDictionary<string, string> mappings)
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

        return new LocalModelProviderResolver(providers, scopeFactory, defaultProviderName, maxLoadedProcesses: 3, timeProvider: TimeProvider.System);
    }

    /// <summary>
    ///     A provider that records every (model) it was asked to build a chat client for and hands back a fresh
    ///     <see cref="StubChatClient" /> per call — standing in for a llama-server deferred client.
    /// </summary>
    private sealed class RecordingLocalModelProvider : ILocalModelProvider
    {
        private readonly string _providerName;

        public RecordingLocalModelProvider(string providerName)
        {
            _providerName = providerName;
        }

        public List<RecordingChatClient> CreatedClients { get; } = [];

        public string ProviderName => _providerName;

        public IChatClient CreateChatClient(LocalModelSelection selection)
        {
            AssertEx.Equal(_providerName, selection.ProviderName);
            var client = new RecordingChatClient(selection.ModelName);
            CreatedClients.Add(client);
            return client;
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

    /// <summary>A stub chat client that remembers the model it was created for so the routing assertions can read it.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string _modelName;

        public RecordingChatClient(string modelName)
        {
            _modelName = modelName;
        }

        public string ModelName => _modelName;

        public int CallCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
