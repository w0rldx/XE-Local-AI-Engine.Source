namespace XE_Local_AI_Engine.Tests.Capabilities;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class CapabilityReporterTests
{
    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenModelInList_ReturnsTrue()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");

        AssertEx.True(result);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenModelMissingAndDefaultConfigured_ReturnsTrue()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("llama3:latest");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("unknown-model");

        AssertEx.True(result);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenDefaultModelIsStored_UsesTheStoredValueNotTheAppsettingsSeed()
    {
        // The reporter used to read Agent:LocalChat:DefaultModel straight from IConfiguration in its constructor, which
        // meant a stored default model was never consulted — the appsettings seed always won, forever. Here the ONLY
        // installed model is the stored default, so this can only pass if the stored value is what gets resolved.
        await using var context = await CreateContextAsync(nodeSettings: new StoredNodeSettings
        {
            DefaultModelName = "unsloth/gemma-4-12b-it-GGUF:Q5_K_M"
        });
        context.SetModelsResponse("unsloth/gemma-4-12b-it-GGUF:Q5_K_M");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("some-unrequested-model");

        AssertEx.True(result, "The stored default model is installed, so the node can fall back to it.");
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenStoredDefaultIsAbsent_DoesNotFallBackToTheSeed()
    {
        // The discriminating case. The appsettings seed ("qwen3.5:0.8b") IS installed and the stored default is NOT, so
        // the two possible implementations give OPPOSITE answers: reading the seed returns true, reading the stored
        // value returns false. Anything but false here means the stored setting is being ignored again.
        await using var context = await CreateContextAsync(nodeSettings: new StoredNodeSettings
        {
            DefaultModelName = "an-uninstalled-stored-default"
        });
        context.SetModelsResponse("qwen3.5:0.8b");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("some-unrequested-model");

        AssertEx.False(result,
            "The operator's stored default model is not installed, so the node must NOT report a usable fallback just "
            + "because the appsettings seed happens to be installed.");
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenOllamaListFailsAndModelConfigured_ReturnsTrue()
    {
        await using var context = await CreateContextAsync();
        context.EnqueueFailure(FakeOllamaFailure.Http500);

        var result = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");

        AssertEx.True(result);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenCalledRepeatedly_UsesCachedInstalledModels()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        var firstResult = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");
        var secondResult = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");

        AssertEx.True(firstResult);
        AssertEx.True(secondResult);
        AssertEx.Equal(expected: 1, context.TagsRequestCount);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenCacheExpires_RefreshesInstalledModels()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        var firstResult = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");
        context.TimeProvider.Advance(TimeSpan.FromSeconds(11));
        var secondResult = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");

        AssertEx.True(firstResult);
        AssertEx.True(secondResult);
        AssertEx.Equal(expected: 2, context.TagsRequestCount);
    }

    private static async Task<CapabilityReporterTestContext> CreateContextAsync(StoredNodeSettings? nodeSettings = null,
        Dictionary<string, string?>? configurationOverrides = null)
    {
        var configurationValues = new Dictionary<string, string?>
        {
            ["Ollama:ChatModel"] = "qwen3.5:0.8b"
        };

        if (configurationOverrides is not null)
        {
            foreach (var (key, value) in configurationOverrides)
            {
                configurationValues[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(configurationValues)
                            .Build();

        var server = await FakeOllamaServer.StartAsync();
        var chatClient = new OllamaApiClient(server.BaseAddress);
        var capabilityClient = new OllamaModelCapabilityClient(chatClient);

        var nodeSettingsStore = new StubNodeSettingsStore(nodeSettings ?? new StoredNodeSettings());
        var timeProvider = new FakeTimeProvider();
        var prober = new ModelCapabilityProber(capabilityClient, configuration, timeProvider, NullLogger<ModelCapabilityProber>.Instance);
        // The REAL NodeRuntimeSettings over the same store, so the reporter resolves its default model through the
        // stored > seed precedence exactly as it does in production. LocalChatAgentOptions.DefaultModel defaults to
        // "qwen3.5:0.8b", the same value these tests put in Ollama:ChatModel, so the seed path is unchanged; what is now
        // additionally honoured is a StoredNodeSettings.DefaultModelName, which the reporter previously ignored outright.
        var runtimeSettings = new NodeRuntimeSettings(nodeSettingsStore,
            configuration,
            Options.Create(new LocalChatAgentOptions()),
            Options.Create(new AgentHomeOptions()),
            Options.Create(new WorkerNodeOptions
            {
                NodeName = "test-node"
            }));
        var reporter = new CapabilityReporter(prober, runtimeSettings, NullLogger<CapabilityReporter>.Instance);
        return new CapabilityReporterTestContext(server, chatClient, reporter, timeProvider);
    }

    private sealed class CapabilityReporterTestContext : IAsyncDisposable
    {
        public CapabilityReporterTestContext(FakeOllamaServer server,
            OllamaApiClient chatClient,
            CapabilityReporter reporter,
            FakeTimeProvider timeProvider)
        {
            Server = server;
            ChatClient = chatClient;
            Reporter = reporter;
            TimeProvider = timeProvider;
        }

        public FakeOllamaServer Server { get; }

        public int TagsRequestCount => Server.RecordedRequests.Count(request => string.Equals(request.Path, "/api/tags", StringComparison.OrdinalIgnoreCase));

        public OllamaApiClient ChatClient { get; }

        public CapabilityReporter Reporter { get; }

        public FakeTimeProvider TimeProvider { get; }

        public async ValueTask DisposeAsync()
        {
            if (ChatClient is IDisposable disposableChatClient)
            {
                disposableChatClient.Dispose();
            }

            await Server.DisposeAsync();
        }

        public void SetModelsResponse(params string[] models)
        {
            ArgumentNullException.ThrowIfNull(models);
            Server.State.Models = models.ToArray();
        }

        public void EnqueueFailure(FakeOllamaFailure failure)
        {
            Server.State.EnqueueFailure(failure);
        }
    }

    private sealed class StubNodeSettingsStore : INodeSettingsStore
    {
        private readonly StoredNodeSettings _settings;

        public StubNodeSettingsStore(StoredNodeSettings settings)
        {
            _settings = settings;
        }

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_settings);
        }

        public StoredNodeSettings Load(CancellationToken cancellationToken = default)
        {
            return _settings;
        }

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<StoredNodeSettings> UpdateAsync(Func<StoredNodeSettings, StoredNodeSettings> mutate, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(mutate(_settings));
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow = _utcNow.Add(timeSpan);
        }
    }
}
