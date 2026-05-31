namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Application service for i connection control behavior.
/// </summary>
public interface IConnectionControlService
{
    Task<ConnectionControlStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<ConnectionControlStatus> ConnectAsync(CancellationToken cancellationToken = default);

    Task<ConnectionControlStatus> DisconnectAsync(CancellationToken cancellationToken = default);

    Task<ConnectionControlStatus> SetAutoConnectAsync(bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
///     Value object carrying connection control status data.
/// </summary>
public sealed record ConnectionControlStatus
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
