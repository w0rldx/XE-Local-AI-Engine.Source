namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Connection;

internal static class ConnectionMapper
{
    public static ConnectionStatusResponse ToResponse(this ConnectionControlStatus status)
    {
        return new ConnectionStatusResponse
        {
            State = status.State,
            LastError = status.LastError,
            LastUpdatedAt = status.LastUpdatedAt,
            IsPaired = status.IsPaired,
            AutoConnectOnStart = status.AutoConnectOnStart,
            BindingMethod = status.BindingMethod,
            LastKnownNodeName = status.LastKnownNodeName,
            TokenExpiresAt = status.TokenExpiresAt,
            CanConnect = status.CanConnect,
            CanDisconnect = status.CanDisconnect,
            CanEnableAutoConnect = status.CanEnableAutoConnect,
            CanDisableAutoConnect = status.CanDisableAutoConnect
        };
    }
}
