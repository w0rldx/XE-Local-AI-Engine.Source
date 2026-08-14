namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Proves the model-routing client's reconnect property: it dispatches each send by
///     <see cref="ChatOptions.ModelId" /> to the provider the persisted map names (or the default for unmapped models),
///     reaches a NEW per-model client when the model switches mid-session (no node restart), caches one client per
///     (provider, model), and never disposes a resolved adapter at the boundary.
/// </summary>
public sealed class ModelRoutingLocalChatClientTests
{
    private const string DefaultModel = "default-chat";

    private const string LlamaProvider = "llamacpp";
    private const string OllamaProvider = "ollama";

    private static ChatMessage[] Message => [new(ChatRole.User, "hi")];

    [Test]
    public async Task GetResponse_RoutesByModelId_ToMappedProviderAndDistinctPerModelClient()
    {
        var llamacpp = new RecordingLocalModelProvider(LlamaProvider);
        var ollama = new RecordingLocalModelProvider(OllamaProvider);
        // "gguf-model" is mapped to llamacpp; "ollama-model" has no row → routes to the default provider (ollama).
        var resolver = BuildResolver([llamacpp, ollama],
            OllamaProvider,
            new Dictionary<string, string>
            {
                ["gguf-model"] = LlamaProvider
            });
        using var router = new ModelRoutingLocalChatClient(resolver, DefaultModel);

        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "gguf-model"
        });
        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "ollama-model"
        });

        // Each model id reached the provider the map (or default) names, with the model carried through the selection.
        AssertEx.Equal(expected: 1, llamacpp.CreatedClients.Count);
        AssertEx.Equal("gguf-model", llamacpp.CreatedClients[0].ModelName);
        AssertEx.Equal(expected: 1, ollama.CreatedClients.Count);
        AssertEx.Equal("ollama-model", ollama.CreatedClients[0].ModelName);

        // The two sends reached two DISTINCT per-model clients (distinct processes) — a model switch mid-session does
        // not reuse the previous model's client.
        AssertEx.False(ReferenceEquals(llamacpp.CreatedClients[0], ollama.CreatedClients[0]),
            "Two different models must resolve to two distinct per-model clients.");
    }

    [Test]
    public async Task GetResponse_WhenSameModelSentTwice_ReusesOneCachedClientPerProviderAndModel()
    {
        var ollama = new RecordingLocalModelProvider(OllamaProvider);
        var resolver = BuildResolver([ollama], OllamaProvider, new Dictionary<string, string>());
        using var router = new ModelRoutingLocalChatClient(resolver, DefaultModel);

        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "chat-a"
        });
        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "chat-a"
        });

        // One CreateChatClient for two sends of the same model — the (provider, model) cache held.
        AssertEx.Equal(expected: 1, ollama.CreatedClients.Count);
        AssertEx.Equal(expected: 2, ollama.CreatedClients[0].CallCount);
        AssertEx.False(ollama.CreatedClients[0].IsDisposed, "A cached client must not be disposed between sends.");
    }

    [Test]
    public async Task GetResponse_WhenModelIdOmitted_FallsBackToDefaultModel()
    {
        var ollama = new RecordingLocalModelProvider(OllamaProvider);
        var resolver = BuildResolver([ollama], OllamaProvider, new Dictionary<string, string>());
        using var router = new ModelRoutingLocalChatClient(resolver, DefaultModel);

        await router.GetResponseAsync(Message);

        AssertEx.Equal(expected: 1, ollama.CreatedClients.Count);
        AssertEx.Equal(DefaultModel, ollama.CreatedClients[0].ModelName);
    }

    [Test]
    public async Task Dispose_DisposesCachedClients_ButNeverDuringASend()
    {
        var ollama = new RecordingLocalModelProvider(OllamaProvider);
        var resolver = BuildResolver([ollama], OllamaProvider, new Dictionary<string, string>());
        var router = new ModelRoutingLocalChatClient(resolver, DefaultModel);

        await router.GetResponseAsync(Message, new ChatOptions
        {
            ModelId = "chat-a"
        });
        var resolvedDuringSend = ollama.CreatedClients[0];
        AssertEx.False(resolvedDuringSend.IsDisposed, "The resolved adapter must survive the send (router never disposes per-call).");

        router.Dispose();

        AssertEx.True(resolvedDuringSend.IsDisposed, "Disposing the router disposes the clients it cached.");
    }

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

        return new LocalModelProviderResolver(providers, scopeFactory, defaultProviderName, maxLoadedProcesses: 3);
    }

    /// <summary>
    ///     A provider that records every (model) it was asked to build a chat client for and hands back a fresh
    ///     <see cref="StubChatClient" /> per call — standing in for a llama-server deferred client or the Ollama client.
    /// </summary>
    private sealed class RecordingLocalModelProvider(string providerName) : ILocalModelProvider
    {
        public List<RecordingChatClient> CreatedClients { get; } = [];

        public string ProviderName => providerName;

        public IChatClient CreateChatClient(LocalModelSelection selection)
        {
            AssertEx.Equal(providerName, selection.ProviderName);
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
    private sealed class RecordingChatClient(string modelName) : IChatClient
    {
        public string ModelName => modelName;

        public int CallCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
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
