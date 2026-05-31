namespace XE_Local_AI_Engine.Client.Endpoints.Connection.V1;

/// <summary>
///     Response DTO for connection status operations.
/// </summary>
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
