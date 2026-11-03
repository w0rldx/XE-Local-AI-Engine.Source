namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;
using XE_Local_AI_Engine.Testing.FakeOpenAiGateway;

/// <summary>
///     The test hosts' <c>llamacpp</c> provider: real OpenAI-compatible clients, built by the product's own factory, over
///     real HTTP to a <see cref="FakeOpenAiGatewayServer" />. Warming and unloading are no-ops: there is no process.
/// </summary>
/// <remarks>Linked into the E2E project, as <see cref="LoopbackPort" /> is, so both factories share one definition.</remarks>
public sealed class FakeGatewayLocalModelProvider : ILocalModelProvider
{
    /// <summary>The model a test host's chat default names.</summary>
    public const string ChatModel = "qwen3.5:0.8b";

    /// <summary>The model a test host embeds with.</summary>
    public const string EmbeddingModel = "qwen3-embedding:0.6b";

    /// <summary>What an unscripted reply starts with; the rest is the last user message.</summary>
    public const string EchoPrefix = "[fake-gateway]";

    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);

    private readonly FakeOpenAiGatewayServer _gateway;

    public FakeGatewayLocalModelProvider(FakeOpenAiGatewayServer gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
    }

    public string ProviderName => LlamaServerProviderConstants.ProviderName;

    /// <summary>The model list a test host starts with.</summary>
    public static IReadOnlyList<FakeOpenAiGatewayModel> DefaultModels { get; } =
    [
        Model(ChatModel),
        Model(EmbeddingModel)
    ];

    /// <summary>The gateway a test host starts: any model is served, because tests choose their own model ids.</summary>
    public static FakeOpenAiGatewayOptions DefaultOptions()
    {
        return new FakeOpenAiGatewayOptions
        {
            Models = DefaultModels,
            AllowUnlistedModels = true,
            EchoPrefix = EchoPrefix
        };
    }

    /// <summary>A model-list entry with the context window every test host model declares.</summary>
    public static FakeOpenAiGatewayModel Model(string id)
    {
        return new FakeOpenAiGatewayModel
        {
            Id = id,
            ContextLength = 32768
        };
    }

    /// <summary>The gateway's model list as installed-model descriptors, tool-capable like a real tool-template GGUF.</summary>
    public static IReadOnlyList<LocalModelDescriptor> Describe(FakeOpenAiGatewayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return
        [
            .. state.Models.Select(static model => new LocalModelDescriptor
            {
                ModelName = model.Id,
                ProviderName = LlamaServerProviderConstants.ProviderName,
                IsAvailable = true,
                SizeBytes = 1024,
                ModifiedAt = null,
                MaxContextTokens = model.ContextLength,
                IsToolCapable = true,
                Capabilities = ["completion", "tools"]
            })
        ];
    }

    public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct)
    {
        return Task.FromResult(new ModelProviderHealth
        {
            ProviderName = ProviderName,
            IsHealthy = true,
            ObservedAt = DateTimeOffset.UtcNow,
            Diagnostics = []
        });
    }

    public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
    {
        return Task.FromResult(Describe(_gateway.State));
    }

    public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task WarmModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task UnloadModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public IChatClient CreateChatClient(LocalModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return OpenAICompatibleClientFactory.CreateChatClient(_gateway.BaseAddress, selection.ModelName, apiKey: null, NetworkTimeout);
    }

    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return OpenAICompatibleClientFactory.CreateEmbeddingGenerator(_gateway.BaseAddress, selection.ModelName, apiKey: null, NetworkTimeout);
    }
}
