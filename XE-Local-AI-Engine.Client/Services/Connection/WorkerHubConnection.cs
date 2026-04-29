namespace XE_Local_AI_Engine.Client.Services.Connection;

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

public sealed class WorkerHubConnection : IWorkerHubConnection
{
    private readonly Lazy<ICapabilityReporter> _capabilityReporter;
    private readonly Action<HttpConnectionOptions>? _configureHttpConnectionOptions;
    private readonly ConnectionState _connectionState;
    private readonly DeadLetterFlushService _deadLetterFlushService;
    private readonly ILogger<WorkerHubConnection> _logger;
    private readonly INodeKeyRegistry _nodeKeyRegistry;
    private readonly IOptions<CentralPlatformOptions> _platformOptions;
    private readonly ITokenStore _tokenStore;
    private readonly IWorkerTokenRefreshService _workerTokenRefreshService;

    private HubConnection? _hubConnection;

    public WorkerHubConnection(ITokenStore tokenStore,
        IOptions<CentralPlatformOptions> platformOptions,
        ConnectionState connectionState,
        Lazy<ICapabilityReporter> capabilityReporter,
        DeadLetterFlushService deadLetterFlushService,
        INodeKeyRegistry nodeKeyRegistry,
        ILogger<WorkerHubConnection> logger,
        Action<HttpConnectionOptions>? configureHttpConnectionOptions = null,
        IWorkerTokenRefreshService? workerTokenRefreshService = null)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _platformOptions = platformOptions ?? throw new ArgumentNullException(nameof(platformOptions));
        _workerTokenRefreshService = workerTokenRefreshService ?? NoOpWorkerTokenRefreshService.Instance;
        _connectionState = connectionState ?? throw new ArgumentNullException(nameof(connectionState));
        _capabilityReporter = capabilityReporter ?? throw new ArgumentNullException(nameof(capabilityReporter));
        _deadLetterFlushService = deadLetterFlushService ?? throw new ArgumentNullException(nameof(deadLetterFlushService));
        _nodeKeyRegistry = nodeKeyRegistry ?? throw new ArgumentNullException(nameof(nodeKeyRegistry));
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
            _logger.LogInformation("Worker token is expired. Attempting refresh before connecting.");
            var refreshed = await _workerTokenRefreshService.TryRefreshAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed || _tokenStore.IsTokenExpired)
            {
                throw new WorkerTokenExpiredException();
            }
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
        connection.On("CapabilitiesReportRequested", ReportCapabilitiesRequestedAsync);
        connection.On<JsonElement>("InvocationAssigned",
            raw =>
            {
                _logger.LogInformation("InvocationAssigned raw frame received. RawJson={RawJson}",
                    raw.GetRawText());

                EncryptedRuntimePackageDto package;
                try
                {
                    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters =
                        {
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                        }
                    };
                    package = raw.Deserialize<EncryptedRuntimePackageDto>(options)
                              ?? throw new InvalidOperationException("Deserialized EncryptedRuntimePackageDto was null.");
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception,
                        "InvocationAssigned manual deserialization failed. RawJson={RawJson}",
                        raw.GetRawText());
                    return;
                }

                _logger.LogInformation("InvocationAssigned typed binding succeeded. InvocationId={InvocationId} ConversationId={ConversationId} MessageId={MessageId} EpochVersion={EpochVersion}",
                    package.InvocationId,
                    package.ConversationId,
                    package.MessageId,
                    package.EpochVersion);

                var handler = InvocationAssignedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("InvocationAssigned received but no handler subscribed. InvocationId={InvocationId}", package.InvocationId);
                    return;
                }

                _logger.LogDebug("Dispatching InvocationAssigned to subscribers. InvocationId={InvocationId}", package.InvocationId);
                handler.Invoke(this, new InvocationAssignedReceivedEventArgs(package));
            });
        connection.On<ToolCallResultEvent>("ToolCallResult",
            evt =>
            {
                _logger.LogInformation("ToolCallResult received. RequestId={RequestId} HasError={HasError}",
                    evt.RequestId,
                    !string.IsNullOrWhiteSpace(evt.Error));

                var handler = ToolCallResultReceived;
                if (handler is null)
                {
                    _logger.LogWarning("ToolCallResult received but no handler subscribed. RequestId={RequestId}", evt.RequestId);
                    return;
                }

                _logger.LogDebug("Dispatching ToolCallResult to subscribers. RequestId={RequestId}", evt.RequestId);
                handler.Invoke(this, new ToolCallResultReceivedEventArgs(evt));
            });
        connection.On<DisconnectRequestedEvent>("DisconnectRequested",
            evt =>
            {
                _logger.LogInformation("DisconnectRequested received. Reason={Reason}", evt.Reason);

                var handler = DisconnectRequestedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("DisconnectRequested received but no handler subscribed. Reason={Reason}", evt.Reason);
                    return;
                }

                _logger.LogDebug("Dispatching DisconnectRequested to subscribers. Reason={Reason}", evt.Reason);
                handler.Invoke(this, new DisconnectRequestedReceivedEventArgs(evt));
            });
        connection.On<ApprovalResolvedEvent>("ApprovalResolved",
            evt =>
            {
                _logger.LogInformation("ApprovalResolved received. RequestId={RequestId} Approved={Approved}",
                    evt.RequestId,
                    evt.Approved);

                var handler = ApprovalResolvedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("ApprovalResolved received but no handler subscribed. RequestId={RequestId}", evt.RequestId);
                    return;
                }

                _logger.LogDebug("Dispatching ApprovalResolved to subscribers. RequestId={RequestId}", evt.RequestId);
                handler.Invoke(this, new ApprovalResolvedReceivedEventArgs(evt));
            });
        connection.On<InvocationCancelledEvent>("InvocationCancelled",
            evt =>
            {
                _logger.LogInformation("InvocationCancelled received. InvocationId={InvocationId} Reason={Reason}",
                    evt.InvocationId,
                    evt.Reason);

                var handler = InvocationCancelledReceived;
                if (handler is null)
                {
                    _logger.LogWarning("InvocationCancelled received but no handler subscribed. InvocationId={InvocationId}", evt.InvocationId);
                    return;
                }

                _logger.LogDebug("Dispatching InvocationCancelled to subscribers. InvocationId={InvocationId}", evt.InvocationId);
                handler.Invoke(this, new InvocationCancelledReceivedEventArgs(evt));
            });
        connection.On<Guid>("ConversationPurged",
            conversationId =>
            {
                _logger.LogInformation("ConversationPurged received. ConversationId={ConversationId}", conversationId);

                var handler = ConversationPurgedReceived;
                if (handler is null)
                {
                    _logger.LogWarning("ConversationPurged received but no handler subscribed. ConversationId={ConversationId}", conversationId);
                    return;
                }

                _logger.LogDebug("Dispatching ConversationPurged to subscribers. ConversationId={ConversationId}", conversationId);
                handler.Invoke(this,
                    new ConversationPurgedReceivedEventArgs(new ConversationPurgedEvent
                    {
                        ConversationId = conversationId
                    }));
            });
    }

    private async Task ReportCapabilitiesRequestedAsync()
    {
        try
        {
            _logger.LogInformation("Capabilities report requested by central platform.");
            await _capabilityReporter.Value.ReportToApiAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to report capabilities after central platform request.");
        }
    }

    private async Task RegisterNodeKeyAsync(CancellationToken cancellationToken = default)
    {
        var keyId = Guid.NewGuid();
#pragma warning disable CA2000 // NodeKeyRegistry takes ownership and disposes the key
        var privateKey = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
#pragma warning restore CA2000
        _nodeKeyRegistry.Rotate(keyId.ToString("N"), privateKey);

        var publicKeyBytes = _nodeKeyRegistry.ActivePublicKey.Export(KeyBlobFormat.RawPublicKey);
        var publicKeyBase64 = Convert.ToBase64String(publicKeyBytes);
        var popChallenge = keyId.ToString("N");
        var popSignature = publicKeyBase64;

        await SendWorkerKeyRegisteredAsync(keyId, publicKeyBase64, popSignature, popChallenge, cancellationToken).ConfigureAwait(false);
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
            await RegisterNodeKeyAsync().ConfigureAwait(false);
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

    private sealed class NoOpWorkerTokenRefreshService : IWorkerTokenRefreshService
    {
        public static readonly NoOpWorkerTokenRefreshService Instance = new();

        private NoOpWorkerTokenRefreshService()
        {
        }

        public Task<bool> TryRefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    private sealed record ClientCapabilitiesPayload
    {
        public required HardwareCapabilitiesPayload HardwareInfo { get; init; }

        public required SystemCapabilitiesPayload Capabilities { get; init; }

        public string NodeType { get; init; } = "Local";

        public string? CloudProviderName { get; init; }

        public static ClientCapabilitiesPayload From(ClientCapabilities capabilities)
        {
            return new ClientCapabilitiesPayload
            {
                HardwareInfo = new HardwareCapabilitiesPayload
                {
                    RamMb = ToInt32(capabilities.RamMb),
                    VramMb = ToInt32(capabilities.VramMb),
                    CudaAvailable = capabilities.CudaAvailable,
                    GpuName = capabilities.GpuName,
                    CpuClass = capabilities.CpuClass
                },
                Capabilities = new SystemCapabilitiesPayload
                {
                    SystemScoreClass = capabilities.SystemScoreClass ?? "Medium",
                    InstalledModels = capabilities.InstalledModels,
                    SupportedCapabilities = capabilities.SupportedCapabilities,
                    ActiveModel = capabilities.ActiveModel,
                    ActiveModelExpiresAt = capabilities.ActiveModelExpiresAt
                },
                NodeType = capabilities.NodeType,
                CloudProviderName = capabilities.CloudProviderName
            };
        }

        private static int ToInt32(long? value)
        {
            return value is null ? 0 : checked((int)value.Value);
        }
    }

    private sealed record HardwareCapabilitiesPayload
    {
        public int RamMb { get; init; }

        public int VramMb { get; init; }

        public bool CudaAvailable { get; init; }

        public string? GpuName { get; init; }

        public string? CpuClass { get; init; }
    }

    private sealed record SystemCapabilitiesPayload
    {
        public string SystemScoreClass { get; init; } = "Medium";

        public IReadOnlyList<string> InstalledModels { get; init; } = [];

        public IReadOnlyList<string> SupportedCapabilities { get; init; } = [];

        public string? ActiveModel { get; init; }

        public DateTimeOffset? ActiveModelExpiresAt { get; init; }
    }
}
