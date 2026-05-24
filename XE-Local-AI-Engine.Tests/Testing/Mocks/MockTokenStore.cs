namespace XE_Local_AI_Engine.Tests.Testing.Mocks;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class MockTokenStore : ITokenStore
{
    private string? _accessToken;
    private Guid? _clientNodeId;
    private string? _refreshToken;

    public int ClearTokensAsyncCallCount { get; private set; }

    public int HandleKeyRotationAsyncCallCount { get; private set; }

    public int StoreTokensAsyncCallCount { get; private set; }

    public event EventHandler? TokensChanged;

    public bool IsPaired => !string.IsNullOrWhiteSpace(_accessToken) && _clientNodeId is not null;

    public bool IsTokenExpired => TokenExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow;

    public bool IsTokenExpiringSoon =>
        TokenExpiresAt is { } expiresAt &&
        expiresAt > DateTimeOffset.UtcNow &&
        expiresAt - DateTimeOffset.UtcNow <= TimeSpan.FromHours(24);

    public DateTimeOffset? TokenExpiresAt { get; private set; }

    public bool AutoConnectOnStart { get; private set; }

    public string? BindingMethod { get; private set; }

    public string? LastKnownNodeName { get; private set; }

    public Task<string?> GetAccessTokenAsync()
    {
        return Task.FromResult(IsTokenExpired ? null : _accessToken);
    }

    public Task<Guid?> GetClientNodeIdAsync()
    {
        return Task.FromResult(_clientNodeId);
    }

    public Task<string?> GetRefreshTokenAsync()
    {
        return Task.FromResult(_refreshToken);
    }

    public Task StoreTokensAsync(PairClientResponse pairingResponse, TokenStoreMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(pairingResponse);

        StoreTokensAsyncCallCount++;
        _accessToken = pairingResponse.AccessToken;
        _refreshToken = pairingResponse.RefreshToken;
        _clientNodeId = pairingResponse.ClientNodeId;
        TokenExpiresAt = pairingResponse.ExpiresAt;
        BindingMethod = metadata?.BindingMethod ?? BindingMethod ?? "pairing-token";
        AutoConnectOnStart = metadata?.AutoConnectOnStart ?? AutoConnectOnStart;
        LastKnownNodeName = metadata?.LastKnownNodeName ?? LastKnownNodeName;
        TokensChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetAutoConnectOnStartAsync(bool enabled)
    {
        AutoConnectOnStart = enabled;
        TokensChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ClearTokensAsync()
    {
        ClearTokensAsyncCallCount++;
        _accessToken = null;
        _refreshToken = null;
        _clientNodeId = null;
        TokenExpiresAt = null;
        TokensChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task HandleKeyRotationAsync()
    {
        HandleKeyRotationAsyncCallCount++;
        return Task.CompletedTask;
    }

    public static MockTokenStore Unpaired()
    {
        return new MockTokenStore();
    }

    public static MockTokenStore Paired(string jwt, Guid clientNodeId, DateTimeOffset expiresAt)
    {
        return new MockTokenStore
        {
            _accessToken = jwt,
            _refreshToken = "refresh-token",
            _clientNodeId = clientNodeId,
            TokenExpiresAt = expiresAt,
            AutoConnectOnStart = true,
            BindingMethod = "pairing-token"
        };
    }

    public static MockTokenStore PairedWithAutoConnectDisabled()
    {
        return new MockTokenStore
        {
            _accessToken = "token",
            _clientNodeId = Guid.NewGuid(),
            TokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            AutoConnectOnStart = false,
            BindingMethod = "device-code"
        };
    }

    public static MockTokenStore WithExpiredToken()
    {
        return Paired("expired-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    public static MockTokenStore WithExpiringSoonToken()
    {
        return Paired("expiring-soon-token", Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(12));
    }
}
