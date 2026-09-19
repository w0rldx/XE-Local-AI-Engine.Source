namespace XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Minimal never-paired <see cref="ITokenStore" /> for the E2E host: no stored worker credentials, so both
///     reads answer null and every caller falls back to the deterministic local-loopback identity.
/// </summary>
public sealed class StubTokenStore : ITokenStore
{
    public Task<string?> GetAccessTokenAsync()
    {
        return Task.FromResult<string?>(null);
    }

    public Task<Guid?> GetClientNodeIdAsync()
    {
        return Task.FromResult<Guid?>(null);
    }
}
