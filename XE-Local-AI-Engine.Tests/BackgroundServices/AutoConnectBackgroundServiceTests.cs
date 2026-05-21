namespace XE_Local_AI_Engine.Tests.BackgroundServices;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.HostAgent;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

[NotInParallel(nameof(AutoConnectBackgroundServiceTests))]
public sealed class AutoConnectBackgroundServiceTests : IDisposable
{
    public void Dispose()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.Zero;
    }

    [Test]
    public async Task ExecuteAsync_WhenPaired_CallsConnectAsync()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection();

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), CreateApplicationLifetime());
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(1, hubConnection.ConnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenNotPaired_SkipsConnect()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection();

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Unpaired(), CreateApplicationLifetime());
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(0, hubConnection.ConnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenTokenExpired_CallsConnectSoConnectionCanRefresh()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection();

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.WithExpiredToken(), CreateApplicationLifetime());
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(1, hubConnection.ConnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenAutoConnectDisabled_SkipsConnect()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection();

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.PairedWithAutoConnectDisabled(), CreateApplicationLifetime());
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(0, hubConnection.ConnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenBootstrapModelIsNotReady_DoesNotConnect()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection();
        var connectionState = new ConnectionState();

        try
        {
            using var service = CreateService(hubConnection,
                MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
                CreateApplicationLifetime(),
                new CentralPlatformOptions
                {
                    BaseUrl = "https://test.example.com",
                    ReconnectBackoffBaseMs = 1,
                    ReconnectBackoffMaxMs = 1,
                    ReconnectBackoffJitterMs = 0,
                    ReconnectMaxAttempts = 1
                },
                new SequenceHostAgentReadinessClient(false, false),
                connectionState);
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(0, hubConnection.ConnectAsyncCallCount);
            AssertEx.Equal(WorkerConnectionState.PreparingModel, connectionState.Current);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenBootstrapModelBecomesReady_ConnectsAfterGateOpens()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection();

        try
        {
            using var service = CreateService(hubConnection,
                MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
                CreateApplicationLifetime(),
                new CentralPlatformOptions
                {
                    BaseUrl = "https://test.example.com",
                    ReconnectBackoffBaseMs = 1,
                    ReconnectBackoffMaxMs = 1,
                    ReconnectBackoffJitterMs = 0,
                    ReconnectMaxAttempts = 3
                },
                new SequenceHostAgentReadinessClient(false, true));
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(1, hubConnection.ConnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ExecuteAsync_WhenConnectThrows_RetriesWithBackoff()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(1);
        var hubConnection = new MockWorkerHubConnection
        {
            ConnectException = new InvalidOperationException("boom")
        };

        try
        {
            using var service = CreateService(hubConnection,
                MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
                CreateApplicationLifetime(),
                new CentralPlatformOptions
                {
                    BaseUrl = "https://test.example.com",
                    ReconnectBackoffBaseMs = 1,
                    ReconnectBackoffMaxMs = 1,
                    ReconnectBackoffJitterMs = 0,
                    ReconnectMaxAttempts = 3
                });
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.CancelAfter(1000);

            await BackgroundServiceTestHelper.RunExecuteAsync(service, cancellationTokenSource.Token);

            AssertEx.Equal(4, hubConnection.ConnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task StopAsync_CancelsInternalToken_NoDisconnectCall()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(50);
        var hubConnection = new MockWorkerHubConnection();

        try
        {
            using var service = CreateService(hubConnection, MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), CreateApplicationLifetime());

            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);

            AssertEx.Equal(0, hubConnection.DisconnectAsyncCallCount);
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public async Task ApplicationStopping_StopsAcceptingRemoteInvocations()
    {
        AutoConnectBackgroundService.TestStartupDelayOverride = TimeSpan.FromMilliseconds(50);
        var hubConnection = new MockWorkerHubConnection();
        using var applicationLifetime = new MockHostApplicationLifetime();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();

        try
        {
            using var service = CreateService(hubConnection,
                MockTokenStore.Paired("token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)),
                applicationLifetime,
                workerEventDispatcher: dispatcher);

            await service.StartAsync(CancellationToken.None);
            applicationLifetime.StopApplication();
            await service.StopAsync(CancellationToken.None);

            dispatcher.Received(1).StopAcceptingRemoteInvocations();
        }
        finally
        {
            await hubConnection.DisposeAsync();
        }
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var hubConnection = new MockWorkerHubConnection();
        using var service = CreateService(hubConnection, MockTokenStore.Unpaired(), CreateApplicationLifetime());
        hubConnection.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static IHostApplicationLifetime CreateApplicationLifetime()
    {
        return new MockHostApplicationLifetime();
    }

    private static AutoConnectBackgroundService CreateService(IWorkerHubConnection hubConnection,
        MockTokenStore tokenStore,
        IHostApplicationLifetime applicationLifetime,
        CentralPlatformOptions? options = null,
        IHostAgentReadinessClient? hostAgentReadinessClient = null,
        ConnectionState? connectionState = null,
        IWorkerEventDispatcher? workerEventDispatcher = null)
    {
        return new AutoConnectBackgroundService(hubConnection,
            tokenStore,
            hostAgentReadinessClient ?? new SequenceHostAgentReadinessClient(true),
            connectionState ?? new ConnectionState(),
            workerEventDispatcher ?? Substitute.For<IWorkerEventDispatcher>(),
            applicationLifetime,
            Options.Create(options ?? new CentralPlatformOptions
            {
                BaseUrl = "https://test.example.com",
                ReconnectBackoffBaseMs = 10,
                ReconnectBackoffMaxMs = 10,
                ReconnectBackoffJitterMs = 0,
                ReconnectMaxAttempts = 3
            }),
            NullLogger<AutoConnectBackgroundService>.Instance);
    }

    private sealed class SequenceHostAgentReadinessClient : IHostAgentReadinessClient
    {
        private readonly Queue<bool> _results;
        private bool _lastResult;

        public SequenceHostAgentReadinessClient(params bool[] results)
        {
            _results = new Queue<bool>(results);
            _lastResult = results.Length > 0 && results[^1];
        }

        public Task<bool> IsBootstrapModelReadyAsync(CancellationToken cancellationToken)
        {
            if (_results.TryDequeue(out var result))
            {
                _lastResult = result;
            }

            return Task.FromResult(_lastResult);
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

        public int ConnectAsyncCallCount { get; private set; }

        public int DisconnectAsyncCallCount { get; private set; }

        public Exception? ConnectException { get; init; }

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
            ConnectAsyncCallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (ConnectException is not null)
            {
                throw ConnectException;
            }

            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectAsyncCallCount++;
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

        public Task SendInvocationKeyMismatchAsync(Guid messageId,
            string reason,
            string nodeKeyIdUsed,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
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

    private sealed class MockHostApplicationLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _applicationStopping = new();

        public void Dispose()
        {
            _applicationStopping.Dispose();
        }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _applicationStopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            _applicationStopping.Cancel();
        }
    }
}
