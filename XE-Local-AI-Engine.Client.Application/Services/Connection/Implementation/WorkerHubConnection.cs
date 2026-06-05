namespace XE_Local_AI_Engine.Client.Services.Connection.Implementation;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using NSec.Cryptography;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;

/// <summary>
///     Represents worker hub connection.
/// </summary>
public sealed partial class WorkerHubConnection : IWorkerHubConnection
{
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(5);
    private readonly Lazy<ICapabilityReporter> _capabilityReporter;
    private readonly Action<HttpConnectionOptions>? _configureHttpConnectionOptions;
    private readonly ConnectionState _connectionState;
    private readonly DeadLetterFlushService _deadLetterFlushService;
    private readonly ILogger<WorkerHubConnection> _logger;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly IOptions<CentralPlatformOptions> _platformOptions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private readonly ITokenStore _tokenStore;
    private readonly IWorkerTokenRefreshService _workerTokenRefreshService;

    private bool _credentialsRevoked;
    private HubConnection? _hubConnection;

    public WorkerHubConnection(ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> platformOptions,
        ConnectionState connectionState,
        Lazy<ICapabilityReporter> capabilityReporter,
        DeadLetterFlushService deadLetterFlushService,
        INodeKeyRegistry nodeKeyRegistry,
        ILogger<WorkerHubConnection> logger,
        Action<HttpConnectionOptions>? configureHttpConnectionOptions = null,
        IWorkerTokenRefreshService? workerTokenRefreshService = null,
        TimeProvider? timeProvider = null)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _platformOptions = platformOptions ?? throw new ArgumentNullException(nameof(platformOptions));
        _workerTokenRefreshService = workerTokenRefreshService ?? NoOpWorkerTokenRefreshService.Instance;
        _connectionState = connectionState ?? throw new ArgumentNullException(nameof(connectionState));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _deadLetterFlushService = deadLetterFlushService ?? throw new ArgumentNullException(nameof(deadLetterFlushService));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _configureHttpConnectionOptions = configureHttpConnectionOptions;

        _connectionState.StateChanged += OnConnectionStateChanged;
    }

    public event EventHandler<WorkerConnectionStateChangedEventArgs>? StateChanged;

    public event EventHandler<InvocationAssignedReceivedEventArgs>? InvocationAssignedReceived;

    public event EventHandler<ToolCallResultReceivedEventArgs>? ToolCallResultReceived;

    public event EventHandler<DisconnectRequestedReceivedEventArgs>? DisconnectRequestedReceived;

    public event EventHandler<ApprovalResolvedReceivedEventArgs>? ApprovalResolvedReceived;

    public event EventHandler<InvocationCancelledReceivedEventArgs>? InvocationCancelledReceived;

    public event EventHandler<ConversationPurgedReceivedEventArgs>? ConversationPurgedReceived;

    public WorkerConnectionState State => _connectionState.Current;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State == WorkerConnectionState.Connected)
        {
            return;
        }

        if (!_tokenStore.IsPaired)
        {
            throw new WorkerNotPairedException();
        }

        // A fresh connect attempt (e.g. after re-pairing) clears any prior revocation latch so the new
        // reconnect policy instance is not pre-poisoned.
        Volatile.Write(ref _credentialsRevoked, false);

        bool tokenIsFresh;
        try
        {
            tokenIsFresh = await EnsureFreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerCredentialsRevokedException exception)
        {
            _logger.LogWarning(exception, "Worker credentials were revoked during initial connect. Re-pairing is required.");
            _connectionState.TransitionTo(WorkerConnectionState.Error, exception.Message);
            throw;
        }

        if (!tokenIsFresh)
        {
            throw new WorkerTokenExpiredException();
        }

        _connectionState.TransitionTo(WorkerConnectionState.Connecting);

        try
        {
            await DisposeHubConnectionAsync().ConfigureAwait(false);

            _hubConnection = CreateHubConnection();
            await _hubConnection.StartAsync(cancellationToken).ConfigureAwait(false);

            var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false);
            if (clientNodeId is null)
            {
                throw new WorkerNotPairedException("Worker pairing is incomplete because no client node id is stored.");
            }

            await SendWorkerHelloAsync(clientNodeId.Value, cancellationToken).ConfigureAwait(false);
            await _capabilityReporter.Value.ReportToApiAsync(cancellationToken).ConfigureAwait(false);
            await RegisterNodeKeyAsync(cancellationToken).ConfigureAwait(false);
            _connectionState.TransitionTo(WorkerConnectionState.Connected);
        }
        catch (Exception exception) when (IsInactiveHubSendException(exception))
        {
            const string message = "Worker hub disconnected during startup handshake. Stored node credentials may be stale or rejected; refresh or re-pairing is required.";
            _logger.LogWarning(exception, message);
            _connectionState.TransitionTo(WorkerConnectionState.Error, message);
            throw new InvalidOperationException(message, exception);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Worker hub connection failed.");
            _connectionState.TransitionTo(WorkerConnectionState.Error, exception.Message);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_hubConnection is null)
        {
            _connectionState.TransitionTo(WorkerConnectionState.Disconnected);
            return;
        }

        try
        {
            await _hubConnection.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeHubConnectionAsync().ConfigureAwait(false);
            _connectionState.TransitionTo(WorkerConnectionState.Disconnected);
        }
    }

    public Task SendWorkerHelloAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
    {
        return SendAsync("WorkerHello", new WorkerHelloPayload
        {
            ClientNodeId = clientNodeId
        }, cancellationToken);
    }

    public Task SendWorkerKeyRegisteredAsync(Guid keyId, string publicKey, string popSignature, string popChallenge, CancellationToken cancellationToken = default)
    {
        return SendAsync("WorkerKeyRegistered", new WorkerKeyRegisteredPayload
        {
            KeyId = keyId,
            PublicKey = publicKey,
            PopSignature = popSignature,
            PopChallenge = popChallenge
        }, cancellationToken);
    }

    public Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return SendAsync("WorkerCapabilitiesReported", ClientCapabilitiesPayload.From(capabilities), cancellationToken);
    }

    public Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
    {
        return SendAsync("Heartbeat",
            new HeartbeatPayload
            {
                ClientNodeId = clientNodeId,
                Timestamp = _timeProvider.GetUtcNow()
            },
            cancellationToken);
    }

    public Task SendPurgeConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        return SendAsync("SendPurgeConversationAsync", conversationId, cancellationToken);
    }

    public Task SendInvocationKeyMismatchAsync(Guid messageId, string reason, string nodeKeyIdUsed, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeKeyIdUsed);

        return SendAsync("InvocationKeyMismatch",
            new InvocationKeyMismatchPayload
            {
                MessageId = messageId,
                Reason = reason,
                NodeKeyIdUsed = nodeKeyIdUsed
            },
            cancellationToken);
    }

    public Task SendInvocationAcceptedAsync(Guid invocationId, CancellationToken cancellationToken = default)
    {
        return SendAsync("InvocationAccepted", new
        {
            InvocationId = invocationId
        }, cancellationToken);
    }

    public Task SendEncryptedChunkAsync(EncryptedChunkEnvelopeV1 payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("SendEncryptedChunkAsync", payload, cancellationToken);
    }

    public Task SendEncryptedCompletedAsync(EncryptedCompletedEnvelopeV1 payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("SendEncryptedCompletedAsync", payload, cancellationToken);
    }

    public Task SendEncryptedFailedAsync(EncryptedFailedEnvelopeV1 payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("SendEncryptedFailedAsync", payload, cancellationToken);
    }

    public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
    {
        return SendAsync("TokenStreamChunk",
            new TokenStreamChunkPayload
            {
                InvocationId = invocationId,
                Token = token,
                IsComplete = isComplete,
                SourceSequence = sourceSequence
            },
            cancellationToken);
    }

    public Task SendReasoningStreamChunkAsync(Guid invocationId, string token, bool isComplete, long? sourceSequence = null, CancellationToken cancellationToken = default)
    {
        return SendAsync("ReasoningStreamChunk",
            new TokenStreamChunkPayload
            {
                InvocationId = invocationId,
                Token = token,
                IsComplete = isComplete,
                SourceSequence = sourceSequence
            },
            cancellationToken);
    }

    public Task SendToolCallRequestAsync(ToolCallRequestPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("ToolCallRequested", payload, cancellationToken);
    }

    public Task SendApprovalRequestAsync(ApprovalRequestPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("ApprovalRequested", payload, cancellationToken);
    }

    public Task SendInvocationCompletedAsync(InvocationCompletedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("InvocationCompleted", payload, cancellationToken);
    }

    public Task SendInvocationFailedAsync(InvocationFailedPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return SendAsync("InvocationFailed", payload, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _connectionState.StateChanged -= OnConnectionStateChanged;
        await DisposeHubConnectionAsync().ConfigureAwait(false);
        _tokenRefreshLock.Dispose();
    }

    private HubConnection CreateHubConnection()
    {
        var options = _platformOptions.Value;
        var hubUrl = BuildHubUrl(options);

        var connection = new HubConnectionBuilder()
                         .WithUrl(hubUrl, httpOptions =>
                         {
                             httpOptions.AccessTokenProvider = GetRequiredAccessTokenAsync;
                             httpOptions.Transports = HttpTransportType.WebSockets | HttpTransportType.LongPolling;
                             _configureHttpConnectionOptions?.Invoke(httpOptions);
                         })
                         .WithAutomaticReconnect(new WorkerReconnectPolicy(options, OnReconnectPolicyDetectedRevokedCredentials))
                         .AddJsonProtocol(jsonOptions =>
                         {
                             jsonOptions.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                             jsonOptions.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                             jsonOptions.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                         })
                         .ConfigureLogging(logging =>
                         {
                             logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
                         })
                         .Build();

        RegisterEventHandlers(connection);
        connection.Closed += OnConnectionClosedAsync;
        connection.Reconnecting += OnReconnectingAsync;
        connection.Reconnected += OnReconnectedAsync;

        return connection;
    }

    private async Task SendAsync(string methodName, object payload, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);
        ArgumentNullException.ThrowIfNull(payload);

        var connection = _hubConnection;
        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            throw new InvalidOperationException($"Worker hub connection is not active. Cannot send '{methodName}'.");
        }

        await connection.SendAsync(methodName, payload, cancellationToken).ConfigureAwait(false);
    }

}
