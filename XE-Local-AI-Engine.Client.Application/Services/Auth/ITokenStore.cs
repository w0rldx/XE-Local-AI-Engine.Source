namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Persistence boundary for i token data.
/// </summary>
public interface ITokenStore
{
    bool IsPaired { get; }

    bool IsTokenExpired { get; }

    bool IsTokenExpiringSoon { get; }

    DateTimeOffset? TokenExpiresAt { get; }

    bool AutoConnectOnStart { get; }

    string? BindingMethod { get; }

    string? LastKnownNodeName { get; }

    Task<string?> GetAccessTokenAsync();

    Task<Guid?> GetClientNodeIdAsync();

    Task<string?> GetRefreshTokenAsync();

    Task StoreTokensAsync(PairClientResponse pairingResponse, TokenStoreMetadata? metadata = null);

    Task SetAutoConnectOnStartAsync(bool enabled);

    Task ClearTokensAsync();

    Task HandleKeyRotationAsync();

    event EventHandler? TokensChanged;
}

/// <summary>
///     Value object carrying token store metadata data.
/// </summary>
public sealed record TokenStoreMetadata
{
    public string? BindingMethod { get; init; }

    public bool? AutoConnectOnStart { get; init; }

    public string? LastKnownNodeName { get; init; }
}
