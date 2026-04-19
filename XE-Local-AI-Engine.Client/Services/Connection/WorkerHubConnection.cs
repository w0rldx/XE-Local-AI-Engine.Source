namespace XE_Local_AI_Engine.Client.Services.Connection;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Encrypted;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.DeadLetter;

public sealed class WorkerHubConnection : IWorkerHubConnection
{
    private readonly Lazy<ICapabilityReporter> _capabilityReporter;
    private readonly ConnectionState _connectionState;
    private readonly DeadLetterFlushService _deadLetterFlushService;
    private readonly ILogger<WorkerHubConnection> _logger;
    private readonly IOptions<CentralPlatformOptions> _platformOptions;
    private readonly ITokenStore _tokenStore;
    private readonly Action<HttpConnectionOptions>? _configureHttpConnectionOptions;

    private HubConnection? _hubConnection;

    public WorkerHubConnection(ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> platformOptions,
        ConnectionState connectionState,
        Lazy<ICapabilityReporter> capabilityReporter,
        DeadLetterFlushService deadLetterFlushService,
        ILogger<WorkerHubConnection> logger,
        Action<HttpConnectionOptions>? configureHttpConnectionOptions = null)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _platformOptions = platformOptions ?? throw new ArgumentNullException(nameof(platformOptions));
        _connectionState = connectionState ?? throw new ArgumentNullException(nameof(connectionState));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _deadLetterFlushService = deadLetterFlushService ?? throw new ArgumentNullException(nameof(deadLetterFlushService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        if (_tokenStore.IsTokenExpired)
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
            _connectionState.TransitionTo(WorkerConnectionState.Connected);
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

    public Task SendCapabilitiesAsync(ClientCapabilities capabilities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return SendAsync("WorkerCapabilitiesReported", capabilities, cancellationToken);
    }

    public Task SendHeartbeatAsync(Guid clientNodeId, CancellationToken cancellationToken = default)
    {
        return SendAsync("Heartbeat",
            new HeartbeatPayload
            {
                ClientNodeId = clientNodeId,
                Timestamp = DateTimeOffset.UtcNow
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

    public Task SendTokenStreamChunkAsync(Guid invocationId, string token, bool isComplete, CancellationToken cancellationToken = default)
    {
        return SendAsync("TokenStreamChunk",
            new TokenStreamChunkPayload
            {
                InvocationId = invocationId,
                Token = token,
                IsComplete = isComplete
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
                         .WithAutomaticReconnect(new WorkerReconnectPolicy(options))
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

    private void RegisterEventHandlers(HubConnection connection)
    {
        connection.On<EncryptedRuntimePackageDto>("InvocationAssigned",
            package => InvocationAssignedReceived?.Invoke(this, new InvocationAssignedReceivedEventArgs(package)));
        connection.On<ToolCallResultEvent>("ToolCallResult",
            evt => ToolCallResultReceived?.Invoke(this, new ToolCallResultReceivedEventArgs(evt)));
        connection.On<DisconnectRequestedEvent>("DisconnectRequested",
            evt => DisconnectRequestedReceived?.Invoke(this, new DisconnectRequestedReceivedEventArgs(evt)));
        connection.On<ApprovalResolvedEvent>("ApprovalResolved",
            evt => ApprovalResolvedReceived?.Invoke(this, new ApprovalResolvedReceivedEventArgs(evt)));
        connection.On<InvocationCancelledEvent>("InvocationCancelled",
            evt => InvocationCancelledReceived?.Invoke(this, new InvocationCancelledReceivedEventArgs(evt)));
        connection.On<Guid>("ConversationPurged",
            conversationId => ConversationPurgedReceived?.Invoke(this,
                new ConversationPurgedReceivedEventArgs(new ConversationPurgedEvent
                {
                    ConversationId = conversationId
                })));
    }

    private async Task<string?> GetRequiredAccessTokenAsync()
    {
        var token = await _tokenStore.GetAccessTokenAsync().ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(token)
            ? token
            : throw new InvalidOperationException("No access token available. Re-pairing is required.");
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

    private async Task DisposeHubConnectionAsync()
    {
        if (_hubConnection is null)
        {
            return;
        }

        var connection = _hubConnection;
        _hubConnection = null;

        connection.Closed -= OnConnectionClosedAsync;
        connection.Reconnecting -= OnReconnectingAsync;
        connection.Reconnected -= OnReconnectedAsync;

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnConnectionClosedAsync(Exception? exception)
    {
        _connectionState.TransitionTo(WorkerConnectionState.Disconnected, exception?.Message);
        return Task.CompletedTask;
    }

    private Task OnReconnectingAsync(Exception? exception)
    {
        _connectionState.TransitionTo(WorkerConnectionState.Reconnecting, exception?.Message);
        return Task.CompletedTask;
    }

    private async Task OnReconnectedAsync(string? connectionId)
    {
        _logger.LogInformation("Worker hub connection reconnected with connection id {ConnectionId}.", connectionId);

        var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false);
        if (clientNodeId is not null)
        {
            await SendWorkerHelloAsync(clientNodeId.Value).ConfigureAwait(false);
            await _capabilityReporter.Value.ReportToApiAsync().ConfigureAwait(false);
            await _deadLetterFlushService.FlushAsync().ConfigureAwait(false);
        }

        _connectionState.TransitionTo(WorkerConnectionState.Connected);
    }

    private void OnConnectionStateChanged(object? sender, WorkerConnectionStateChangedEventArgs eventArgs)
    {
        StateChanged?.Invoke(this, eventArgs);
    }

    private static string BuildHubUrl(CentralPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseUri = new Uri(options.BaseUrl, UriKind.Absolute);
        return new Uri(baseUri, options.HubPath).ToString();
    }
}
