namespace XE_Local_AI_Engine.Tests.Capabilities;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class CapabilityReporterTests
{
    [Test]
    public async Task DetectCapabilitiesAsync_ReturnsNonNullResult()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.NotNull(result);
        AssertEx.Equal(2, result.SchemaVersion);
        AssertEx.True(result.OllamaReachable == true);
        AssertEx.Equal("0.0.0-fake", result.OllamaVersion);
        AssertEx.Equal("unmanaged", result.ManagementMode);
        AssertEx.True(result.LastCapabilityReportAt.HasValue);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_PopulatesInstalledModels()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b", "llava:latest");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.InstalledModels, "qwen3.5:0.8b");
        AssertEx.Contains(result.InstalledModels, "llava:latest");
        AssertEx.Contains(result.SupportedCapabilities, "vision");
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenAgentHomeEnabled_AdvertisesAgentHomeCapabilities()
    {
        await using var context = await CreateContextAsync(configurationOverrides: new Dictionary<string, string?>
        {
            ["AgentHome:Enabled"] = "true"
        });
        context.SetModelsResponse("qwen3.5:0.8b");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.SupportedCapabilities, "text");
        AssertEx.Contains(result.SupportedCapabilities, "agent-home");
        AssertEx.Contains(result.SupportedCapabilities, "sandbox-local-container");
        AssertEx.Contains(result.SupportedCapabilities, "runtime-dotnet-agent-home");
        AssertEx.Contains(result.SupportedCapabilities, "workspace-copy");
        AssertEx.Contains(result.SupportedCapabilities, "patch-export");
        AssertEx.Contains(result.SupportedCapabilities, "memory-proposals");
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenAgentHomeDisabledByDefault_OmitsAgentHomeCapabilities()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.SupportedCapabilities, "text");
        AssertEx.False(result.SupportedCapabilities.Contains("agent-home"),
            "AgentHome capabilities must not be advertised when AgentHome:Enabled is false.");
        AssertEx.False(result.SupportedCapabilities.Contains("sandbox-local-container"));
        AssertEx.False(result.SupportedCapabilities.Contains("memory-proposals"));
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenShowReportsContextLength_PopulatesModelMetadata()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen2:7b");
        context.SetModelDigest("qwen2:7b", "sha256:qwen2-a");
        context.SetModelInfo("qwen2:7b", new Dictionary<string, object?>
        {
            ["qwen2.context_length"] = 32768
        });

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.ContainsSingle(result.InstalledModelMetadata,
            model => string.Equals(model.Name, "qwen2:7b", StringComparison.Ordinal)
                     && string.Equals(model.Digest, "sha256:qwen2-a", StringComparison.Ordinal)
                     && model.MaxContextTokens == 32768);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenShowOmitsContextLength_ReportsNullModelMetadata()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("fake:latest");
        context.SetModelDigest("fake:latest", "sha256:fake-a");
        context.SetModelInfo("fake:latest", new Dictionary<string, object?>
        {
            ["fake.embedding_length"] = 1024
        });

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.ContainsSingle(result.InstalledModelMetadata,
            model => string.Equals(model.Name, "fake:latest", StringComparison.Ordinal)
                     && string.Equals(model.Digest, "sha256:fake-a", StringComparison.Ordinal)
                     && model.MaxContextTokens is null);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenModelDigestUnchanged_UsesCachedContextLength()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("gemma3:4b");
        context.SetModelDigest("gemma3:4b", "sha256:gemma3-a");
        context.SetModelInfo("gemma3:4b", new Dictionary<string, object?>
        {
            ["gemma3.context_length"] = 131072
        });

        var firstResult = await context.Reporter.DetectCapabilitiesAsync();
        context.ClearRecordedRequests();
        var secondResult = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Equal(131072, firstResult.InstalledModelMetadata.Single(model => model.Name == "gemma3:4b").MaxContextTokens);
        AssertEx.Equal(131072, secondResult.InstalledModelMetadata.Single(model => model.Name == "gemma3:4b").MaxContextTokens);
        AssertEx.Equal(0, context.ShowRequestCount);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenAspireConnectionStringsConfigured_IncludesConfiguredModels()
    {
        await using var context = await CreateContextAsync(configurationOverrides: new Dictionary<string, string?>
        {
            ["ConnectionStrings:chat"] = "Endpoint=http://127.0.0.1:11434;Model=qwen3:0.6b",
            ["ConnectionStrings:embeddings"] = "Endpoint=http://127.0.0.1:11434;Model=qwen3-embedding:0.6b"
        });
        context.SetModelsResponse();

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.InstalledModels, "qwen3:0.6b");
        AssertEx.Contains(result.InstalledModels, "qwen3-embedding:0.6b");
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenRuntimeModelSettingsConfigured_IncludesConfiguredModels()
    {
        await using var context = await CreateContextAsync(configurationOverrides: new Dictionary<string, string?>
        {
            ["Ollama:ChatModel"] = "qwen3.5:0.8b",
            ["Agent:LocalChat:DefaultModel"] = "qwen3.5:0.8b",
            ["Aspire:OllamaSharp:chat:SelectedModel"] = "qwen3:0.6b",
            ["Aspire:OllamaSharp:embeddings:SelectedModel"] = "qwen3-embedding:0.6b"
        });
        context.SetModelsResponse();

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.InstalledModels, "qwen3.5:0.8b");
        AssertEx.Contains(result.InstalledModels, "qwen3:0.6b");
        AssertEx.Contains(result.InstalledModels, "qwen3-embedding:0.6b");
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenRunningModelExists_PopulatesActiveModel()
    {
        await using var context = await CreateContextAsync();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        context.SetModelsResponse("qwen3.5:0.8b", "llava:latest");
        context.SetRunningModels((" qwen3.5:0.8b ", expiresAt));

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Equal("qwen3.5:0.8b", result.ActiveModel);
        AssertEx.Equal(expiresAt, result.ActiveModelExpiresAt);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenNoRunningModels_ReturnsNullActiveModel()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");
        context.SetRunningModels();

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Null(result.ActiveModel);
        AssertEx.Null(result.ActiveModelExpiresAt);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenRunningModelQueryFails_ReturnsNullActiveModel()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");
        await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");
        context.EnqueueFailure(FakeOllamaFailure.Http500);

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.InstalledModels, "qwen3.5:0.8b");
        AssertEx.Null(result.ActiveModel);
        AssertEx.Null(result.ActiveModelExpiresAt);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenOllamaThrows_ReturnsConfiguredModels()
    {
        await using var context = await CreateContextAsync();
        context.EnqueueFailure(FakeOllamaFailure.Http500);

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.InstalledModels, "qwen3.5:0.8b");
        AssertEx.Contains(result.SupportedCapabilities, "text");
        AssertEx.Contains(result.Diagnostics, "ollama-unreachable");
        AssertEx.True(result.OllamaReachable == false);
        AssertEx.Equal("unknown", result.ManagementMode);
        AssertEx.Null(result.ActiveModel);
        AssertEx.Null(result.ActiveModelExpiresAt);
    }

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
        AssertEx.Equal(1, context.TagsRequestCount);
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
        AssertEx.Equal(2, context.TagsRequestCount);
    }

    [Test]
    public async Task ReportToApiAsync_CallsSendCapabilitiesAsync()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        await context.Reporter.ReportToApiAsync();

        AssertEx.Equal(1, context.HubConnection.SendCapabilitiesCallCount);
        AssertEx.NotNull(context.HubConnection.LastCapabilities);
        AssertEx.Contains(context.HubConnection.LastCapabilities!.InstalledModels, "qwen3.5:0.8b");
        AssertEx.Equal(300, context.HubConnection.LastCapabilities.MaxMessageRequestTimeoutSeconds);
        AssertEx.True(context.HubConnection.LastCapabilities.LastCapabilityReportAt.HasValue);
    }

    [Test]
    public async Task ReportToApiAsync_WhenCalledRepeatedly_ThrottlesDuplicateReports()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("qwen3.5:0.8b");

        await context.Reporter.ReportToApiAsync();
        await context.Reporter.ReportToApiAsync();

        AssertEx.Equal(1, context.HubConnection.SendCapabilitiesCallCount);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenNodeSettingsExist_IncludesTimeoutSetting()
    {
        await using var context = await CreateContextAsync(nodeSettings: new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = 120
        });
        context.SetModelsResponse("qwen3.5:0.8b");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Equal(120, result.MaxMessageRequestTimeoutSeconds);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenAzureFoundryCredentialsExist_ReportsCloudCapabilities()
    {
        await using var context = await CreateContextAsync(new StoredCloudCredentials
        {
            ProviderName = "AzureFoundry",
            Endpoint = "https://example.openai.azure.com/",
            ApiKey = "test-api-key",
            DeploymentName = "gpt-4o"
        });

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Equal("Cloud", result.NodeType);
        AssertEx.Equal("AzureFoundry", result.CloudProviderName);
        AssertEx.Equal("Cloud", result.SystemScoreClass);
        AssertEx.Equal("unknown", result.ManagementMode);
        AssertEx.Equal("gpt-4o", result.ActiveModel);
        AssertEx.True(result.LastCapabilityReportAt.HasValue);
        AssertEx.Contains(result.InstalledModels, "gpt-4o");
        AssertEx.Contains(result.SupportedCapabilities, "cloud");
        AssertEx.Equal(0, context.TagsRequestCount);
    }

    private static async Task<CapabilityReporterTestContext> CreateContextAsync(StoredCloudCredentials? cloudCredentials = null,
        StoredNodeSettings? nodeSettings = null,
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
        var modelService = new OllamaModelService(chatClient);

        var hubConnection = new MockWorkerHubConnection();
        var cloudCredentialStore = new StubCloudCredentialStore(cloudCredentials);
        var nodeSettingsStore = new StubNodeSettingsStore(nodeSettings ?? new StoredNodeSettings());
        var timeProvider = new FakeTimeProvider();
        var reporter = new CapabilityReporter(chatClient, modelService, cloudCredentialStore, nodeSettingsStore, configuration, hubConnection, timeProvider, NullLogger<CapabilityReporter>.Instance);
        return new CapabilityReporterTestContext(server, chatClient, modelService, hubConnection, reporter, timeProvider);
    }

    private sealed class CapabilityReporterTestContext : IAsyncDisposable
    {
        public CapabilityReporterTestContext(FakeOllamaServer server,
            OllamaApiClient chatClient,
            OllamaModelService modelService,
            MockWorkerHubConnection hubConnection,
            CapabilityReporter reporter,
            FakeTimeProvider timeProvider)
        {
            Server = server;
            ChatClient = chatClient;
            ModelService = modelService;
            HubConnection = hubConnection;
            Reporter = reporter;
            TimeProvider = timeProvider;
        }

        public FakeOllamaServer Server { get; }

        public int TagsRequestCount => Server.RecordedRequests.Count(request => string.Equals(request.Path, "/api/tags", StringComparison.OrdinalIgnoreCase));

        public int ShowRequestCount => Server.RecordedRequests.Count(request => string.Equals(request.Path, "/api/show", StringComparison.OrdinalIgnoreCase));

        public OllamaApiClient ChatClient { get; }

        public OllamaModelService ModelService { get; }

        public MockWorkerHubConnection HubConnection { get; }

        public CapabilityReporter Reporter { get; }

        public FakeTimeProvider TimeProvider { get; }

        public async ValueTask DisposeAsync()
        {
            ModelService.Dispose();

            if (ChatClient is IDisposable disposableChatClient)
            {
                disposableChatClient.Dispose();
            }

            await Server.DisposeAsync().ConfigureAwait(false);
            await HubConnection.DisposeAsync().ConfigureAwait(false);
        }

        public void SetModelsResponse(params string[] models)
        {
            ArgumentNullException.ThrowIfNull(models);
            Server.State.Models = models.ToArray();
        }

        public void SetModelDigest(string model, string digest)
        {
            Server.State.ModelDigests[model] = digest;
        }

        public void SetModelInfo(string model, IReadOnlyDictionary<string, object?> modelInfo)
        {
            Server.State.ModelInfo[model] = modelInfo;
        }

        public void ClearRecordedRequests()
        {
            Server.State.ClearRequests();
        }

        public void SetRunningModels(params (string Name, DateTimeOffset? ExpiresAt)[] models)
        {
            ArgumentNullException.ThrowIfNull(models);
            Server.State.RunningModels = models
                                         .Select(model => new FakeOllamaState.FakeOllamaRunningModel(model.Name, model.ExpiresAt))
                                         .ToArray();
        }

        public void EnqueueFailure(FakeOllamaFailure failure)
        {
            Server.State.EnqueueFailure(failure);
        }
    }

    private sealed class StubCloudCredentialStore : ICloudCredentialStore
    {
        private readonly StoredCloudCredentials? _credentials;

        public StubCloudCredentialStore(StoredCloudCredentials? credentials)
        {
            _credentials = credentials;
        }

        public Task<StoredCloudCredentials?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_credentials);
        }

        public Task SaveAsync(StoredCloudCredentials credentials, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
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

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
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

    private sealed class MockWorkerHubConnection : IWorkerHubConnection
    {
        private EventHandler<ApprovalResolvedReceivedEventArgs>? _approvalResolvedReceived;
        private EventHandler<ConversationPurgedReceivedEventArgs>? _conversationPurgedReceived;
        private EventHandler<DisconnectRequestedReceivedEventArgs>? _disconnectRequestedReceived;
        private EventHandler<InvocationAssignedReceivedEventArgs>? _invocationAssignedReceived;
        private EventHandler<InvocationCancelledReceivedEventArgs>? _invocationCancelledReceived;
        private EventHandler<WorkerConnectionStateChangedEventArgs>? _stateChanged;
        private EventHandler<ToolCallResultReceivedEventArgs>? _toolCallResultReceived;

        public int SendCapabilitiesCallCount { get; private set; }

        public ClientCapabilities? LastCapabilities { get; private set; }

        public WorkerConnectionState State => WorkerConnectionState.Disconnected;

        public event EventHandler<WorkerConnectionStateChangedEventArgs>? StateChanged
        {
            add => _stateChanged += value;
            remove => _stateChanged -= value;
        }

        public event EventHandler<InvocationAssignedReceivedEventArgs>? InvocationAssignedReceived
        {
            add => _invocationAssignedReceived += value;
            remove => _invocationAssignedReceived -= value;
        }

        public event EventHandler<ToolCallResultReceivedEventArgs>? ToolCallResultReceived
        {
            add => _toolCallResultReceived += value;
            remove => _toolCallResultReceived -= value;
        }

        public event EventHandler<DisconnectRequestedReceivedEventArgs>? DisconnectRequestedReceived
        {
            add => _disconnectRequestedReceived += value;
            remove => _disconnectRequestedReceived -= value;
        }

        public event EventHandler<ApprovalResolvedReceivedEventArgs>? ApprovalResolvedReceived
        {
            add => _approvalResolvedReceived += value;
            remove => _approvalResolvedReceived -= value;
        }

        public event EventHandler<InvocationCancelledReceivedEventArgs>? InvocationCancelledReceived
        {
            add => _invocationCancelledReceived += value;
            remove => _invocationCancelledReceived -= value;
        }

        public event EventHandler<ConversationPurgedReceivedEventArgs>? ConversationPurgedReceived
        {
            add => _conversationPurgedReceived += value;
            remove => _conversationPurgedReceived -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendWorkerHelloAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendWorkerKeyRegisteredAsync(Guid keyId, string publicKey, string popSignature, string popChallenge, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            SendCapabilitiesCallCount++;
            LastCapabilities = capabilities;
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationKeyMismatchAsync(Guid messageId,
            string reason,
            string nodeKeyIdUsed,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendPurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendReasoningStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
