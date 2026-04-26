namespace XE_Local_AI_Engine.Tests.Capabilities;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
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
    public async Task DetectCapabilitiesAsync_WhenOllamaThrows_ReturnsEmptyModels()
    {
        await using var context = await CreateContextAsync();
        context.EnqueueFailure(FakeOllamaFailure.Http500);

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Equal(0, result.InstalledModels.Count);
        AssertEx.Contains(result.SupportedCapabilities, "text");
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
    public async Task VerifyOllamaAndModelAsync_WhenModelMissing_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        context.SetModelsResponse("llama3:latest");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("unknown-model");

        AssertEx.False(result);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenOllamaUnreachable_ReturnsFalse()
    {
        await using var context = await CreateContextAsync();
        context.EnqueueFailure(FakeOllamaFailure.Http500);

        var result = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:0.8b");

        AssertEx.False(result);
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
    }

    private static async Task<CapabilityReporterTestContext> CreateContextAsync()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Ollama:ChatModel"] = "qwen3.5:0.8b"
                            })
                            .Build();

        var server = await FakeOllamaServer.StartAsync();
        var chatClient = new OllamaApiClient(server.BaseAddress);

        var hubConnection = new MockWorkerHubConnection();
        var timeProvider = new FakeTimeProvider();
        var reporter = new CapabilityReporter(chatClient, configuration, hubConnection, timeProvider, NullLogger<CapabilityReporter>.Instance);
        return new CapabilityReporterTestContext(server, chatClient, hubConnection, reporter, timeProvider);
    }

    private sealed class CapabilityReporterTestContext : IAsyncDisposable
    {
        public CapabilityReporterTestContext(FakeOllamaServer server,
            OllamaApiClient chatClient,
            MockWorkerHubConnection hubConnection,
            CapabilityReporter reporter,
            FakeTimeProvider timeProvider)
        {
            Server = server;
            ChatClient = chatClient;
            HubConnection = hubConnection;
            Reporter = reporter;
            TimeProvider = timeProvider;
        }

        public FakeOllamaServer Server { get; }

        public int TagsRequestCount => Server.RecordedRequests.Count(request => string.Equals(request.Path, "/api/tags", StringComparison.OrdinalIgnoreCase));

        public OllamaApiClient ChatClient { get; }

        public MockWorkerHubConnection HubConnection { get; }

        public CapabilityReporter Reporter { get; }

        public FakeTimeProvider TimeProvider { get; }

        public async ValueTask DisposeAsync()
        {
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

        public void EnqueueFailure(FakeOllamaFailure failure)
        {
            Server.State.EnqueueFailure(failure);
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

        public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default)
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
