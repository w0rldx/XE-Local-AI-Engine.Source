namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Minimal unpaired <see cref="ITokenStore" /> for wave-1 E2E tests.
///     Mirrors <c>MockTokenStore.Unpaired()</c> (the shared mock lives in the unit-test
///     namespace with 11 references; per plan D3 we keep a local stub instead of moving it).
///     Every accessor reports the never-paired state and the mutating calls are no-ops.
/// </summary>
public sealed class StubTokenStore : ITokenStore
{
    // The stub never mutates token state, so it never raises this event.
#pragma warning disable CS0067 // Event is never used — required by ITokenStore, intentionally inert here.
    public event EventHandler? TokensChanged;
#pragma warning restore CS0067

    public bool IsPaired => false;

    public bool IsTokenExpired => false;

    public bool IsTokenExpiringSoon => false;

    public DateTimeOffset? TokenExpiresAt => null;

    public bool AutoConnectOnStart => false;

    public string? BindingMethod => null;

    public string? LastKnownNodeName => null;

    public Task<string?> GetAccessTokenAsync()
    {
        return Task.FromResult<string?>(null);
    }

    public Task<Guid?> GetClientNodeIdAsync()
    {
        return Task.FromResult<Guid?>(null);
    }

    public Task<string?> GetRefreshTokenAsync()
    {
        return Task.FromResult<string?>(null);
    }

    public Task StoreTokensAsync(PairClientResponse pairingResponse, TokenStoreMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(pairingResponse);
        return Task.CompletedTask;
    }

    public Task SetAutoConnectOnStartAsync(bool enabled)
    {
        return Task.CompletedTask;
    }

    public Task ClearTokensAsync()
    {
        return Task.CompletedTask;
    }

    public Task HandleKeyRotationAsync()
    {
        return Task.CompletedTask;
    }
}
