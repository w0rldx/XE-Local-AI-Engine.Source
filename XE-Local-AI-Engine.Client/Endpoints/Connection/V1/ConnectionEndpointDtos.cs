namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1;

using XE_Local_AI_Engine.Client.Services.Connection;

public sealed record ConnectionStatusResponse
{
    public required string State { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset LastUpdatedAt { get; init; }

    public bool IsPaired { get; init; }

    public bool AutoConnectOnStart { get; init; }

    public string? BindingMethod { get; init; }

    public string? LastKnownNodeName { get; init; }

    public DateTimeOffset? TokenExpiresAt { get; init; }

    public bool CanConnect { get; init; }

    public bool CanDisconnect { get; init; }

    public bool CanEnableAutoConnect { get; init; }

    public bool CanDisableAutoConnect { get; init; }
}

internal static class ConnectionEndpointDtoMapper
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
