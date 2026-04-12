namespace XE_Local_AI_Engine.Tests.Capabilities;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class CapabilityReporterTests
{
    [Test]
    public async Task DetectCapabilitiesAsync_ReturnsNonNullResult()
    {
        using var context = CreateContext();
        context.Handler.SetModelsResponse("qwen3.5:9b");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.NotNull(result);
    }

    [Test]
    public async Task DetectCapabilitiesAsync_PopulatesInstalledModels()
    {
        using var context = CreateContext();
        context.Handler.SetModelsResponse("qwen3.5:9b", "llava:latest");

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Contains(result.InstalledModels, "qwen3.5:9b");
        AssertEx.Contains(result.InstalledModels, "llava:latest");
        AssertEx.Contains(result.SupportedCapabilities, "vision");
    }

    [Test]
    public async Task DetectCapabilitiesAsync_WhenOllamaThrows_ReturnsEmptyModels()
    {
        using var context = CreateContext();
        context.Handler.ThrowOnNextRequest(new HttpRequestException("offline"));

        var result = await context.Reporter.DetectCapabilitiesAsync();

        AssertEx.Equal(0, result.InstalledModels.Count);
        AssertEx.Contains(result.SupportedCapabilities, "text");
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenModelInList_ReturnsTrue()
    {
        using var context = CreateContext();
        context.Handler.SetModelsResponse("qwen3.5:9b");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:9b");

        AssertEx.True(result);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenModelMissing_ReturnsFalse()
    {
        using var context = CreateContext();
        context.Handler.SetModelsResponse("llama3:latest");

        var result = await context.Reporter.VerifyOllamaAndModelAsync("unknown-model");

        AssertEx.False(result);
    }

    [Test]
    public async Task VerifyOllamaAndModelAsync_WhenOllamaUnreachable_ReturnsFalse()
    {
        using var context = CreateContext();
        context.Handler.ThrowOnNextRequest(new HttpRequestException("offline"));

        var result = await context.Reporter.VerifyOllamaAndModelAsync("qwen3.5:9b");

        AssertEx.False(result);
    }

    [Test]
    public async Task ReportToApiAsync_CallsSendCapabilitiesAsync()
    {
        using var context = CreateContext();
        context.Handler.SetModelsResponse("qwen3.5:9b");

        await context.Reporter.ReportToApiAsync();

        AssertEx.Equal(1, context.HubConnection.SendCapabilitiesCallCount);
        AssertEx.NotNull(context.HubConnection.LastCapabilities);
        AssertEx.Contains(context.HubConnection.LastCapabilities!.InstalledModels, "qwen3.5:9b");
    }

    private static CapabilityReporterTestContext CreateContext()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Ollama:ChatModel"] = "qwen3.5:9b"
                            })
                            .Build();

        var handler = new MockOllamaHttpHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://fake-ollama/")
        };

        var chatClient = new OllamaApiClient(httpClient);

        var hubConnection = new MockWorkerHubConnection();
        var reporter = new CapabilityReporter(chatClient, configuration, hubConnection, NullLogger<CapabilityReporter>.Instance);
        return new CapabilityReporterTestContext(handler, httpClient, chatClient, hubConnection, reporter);
    }

    private sealed class CapabilityReporterTestContext : IDisposable
    {
        public CapabilityReporterTestContext(MockOllamaHttpHandler handler,
            HttpClient httpClient,
            OllamaApiClient chatClient,
            MockWorkerHubConnection hubConnection,
            CapabilityReporter reporter)
        {
            Handler = handler;
            HttpClient = httpClient;
            ChatClient = chatClient;
            HubConnection = hubConnection;
            Reporter = reporter;
        }

        public MockOllamaHttpHandler Handler { get; }

        public HttpClient HttpClient { get; }

        public OllamaApiClient ChatClient { get; }

        public MockWorkerHubConnection HubConnection { get; }

        public CapabilityReporter Reporter { get; }

        public void Dispose()
        {
            if (ChatClient is IDisposable disposableChatClient)
            {
                disposableChatClient.Dispose();
            }

            HttpClient.Dispose();
            Handler.Dispose();
            HubConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class MockWorkerHubConnection : IWorkerHubConnection
    {
        private EventHandler<ApprovalResolvedReceivedEventArgs>? _approvalResolvedReceived;
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

        public Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
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
