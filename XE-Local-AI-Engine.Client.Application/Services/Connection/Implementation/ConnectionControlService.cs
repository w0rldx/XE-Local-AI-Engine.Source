namespace XE_Local_AI_Engine.Client.Services.Connection.Implementation;

using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ConnectionControlService(
    ConnectionState connectionState,
    IWorkerHubConnection workerHubConnection,
    ITokenStore tokenStore) : IConnectionControlService
{
    private readonly ConnectionState _connectionState = connectionState ?? throw new ArgumentNullException(nameof(connectionState));
    private readonly ITokenStore _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    private readonly IWorkerHubConnection _workerHubConnection = workerHubConnection ?? throw new ArgumentNullException(nameof(workerHubConnection));

    public Task<ConnectionControlStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(BuildStatus());
    }

    public async Task<ConnectionControlStatus> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _workerHubConnection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return BuildStatus();
    }

    public async Task<ConnectionControlStatus> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _workerHubConnection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return BuildStatus();
    }

    public async Task<ConnectionControlStatus> SetAutoConnectAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _tokenStore.SetAutoConnectOnStartAsync(enabled).ConfigureAwait(false);

        if (!enabled && _connectionState.Current != WorkerConnectionState.Disconnected)
        {
            await _workerHubConnection.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        return BuildStatus();
    }

    private ConnectionControlStatus BuildStatus()
    {
        var current = _connectionState.Current;
        var isPaired = _tokenStore.IsPaired;
        var autoConnectOnStart = _tokenStore.AutoConnectOnStart;

        return new ConnectionControlStatus
        {
            State = ToWireState(current),
            LastError = _connectionState.LastError,
            LastUpdatedAt = _connectionState.LastUpdatedAt,
            IsPaired = isPaired,
            AutoConnectOnStart = autoConnectOnStart,
            BindingMethod = _tokenStore.BindingMethod,
            LastKnownNodeName = _tokenStore.LastKnownNodeName,
            TokenExpiresAt = _tokenStore.TokenExpiresAt,
            CanConnect = isPaired && current is WorkerConnectionState.Disconnected or WorkerConnectionState.Error,
            CanDisconnect = current is not WorkerConnectionState.Disconnected,
            CanEnableAutoConnect = isPaired && !autoConnectOnStart,
            CanDisableAutoConnect = autoConnectOnStart || current is not WorkerConnectionState.Disconnected
        };
    }

    private static string ToWireState(WorkerConnectionState state)
    {
        return state switch
        {
            WorkerConnectionState.Disconnected => "disconnected",
            WorkerConnectionState.Connecting => "connecting",
            WorkerConnectionState.Connected => "connected",
            WorkerConnectionState.Reconnecting => "reconnecting",
            WorkerConnectionState.Pairing => "pairing",
            WorkerConnectionState.Error => "error",
            _ => "unknown"
        };
    }
}
