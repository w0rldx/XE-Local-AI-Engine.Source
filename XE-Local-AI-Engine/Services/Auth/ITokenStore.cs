namespace XE_Local_AI_Engine.Services.Auth;

using XE_Local_AI_Engine.Models;

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
