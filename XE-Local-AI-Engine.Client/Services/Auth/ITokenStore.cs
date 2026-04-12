namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Models;

public interface ITokenStore
{
    bool IsPaired { get; }

    bool IsTokenExpired { get; }

    bool IsTokenExpiringSoon { get; }

    DateTimeOffset? TokenExpiresAt { get; }
    Task<string?> GetAccessTokenAsync();

    Task<Guid?> GetClientNodeIdAsync();

    Task StoreTokensAsync(PairClientResponse pairingResponse);

    Task ClearTokensAsync();

    Task HandleKeyRotationAsync();

    event EventHandler? TokensChanged;
}
