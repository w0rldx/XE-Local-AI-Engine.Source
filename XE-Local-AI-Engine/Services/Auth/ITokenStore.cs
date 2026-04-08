namespace XE_Local_AI_Engine.Services.Auth
{
    using System;
    using System.Threading.Tasks;
    using XE_Local_AI_Engine.Models;

    public interface ITokenStore
    {
        Task<string?> GetAccessTokenAsync();

        Task<Guid?> GetClientNodeIdAsync();

        Task StoreTokensAsync(PairClientResponse pairingResponse);

        Task ClearTokensAsync();

        Task HandleKeyRotationAsync();

        bool IsPaired { get; }

        bool IsTokenExpired { get; }

        bool IsTokenExpiringSoon { get; }

        DateTimeOffset? TokenExpiresAt { get; }

        event EventHandler? TokensChanged;
    }
}
