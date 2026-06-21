namespace XE_Local_AI_Engine.Tests.Auth;

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

public sealed class TokenStoreTests : IDisposable
{
    private readonly string _contentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_contentRootPath))
        {
            Directory.Delete(_contentRootPath, true);
        }
    }

    [Test]
    public async Task IsPaired_WhenNothingStored_ReturnsFalse()
    {
        using var tokenStore = CreateTokenStore();

        AssertEx.False(tokenStore.IsPaired);
        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
    }

    [Test]
    public async Task StoreTokensAsync_WhenResponseIsValid_SetsPairedState()
    {
        using var tokenStore = CreateTokenStore();

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

        AssertEx.True(tokenStore.IsPaired);
    }

    [Test]
    public async Task StoreTokensAsync_WritesCredentialsUnderDataDirectory_NotContentRoot()
    {
        // The encrypted credential must land in the per-user data dir the node-data-directory abstraction resolves,
        // never in the shared/shipped install (content-root) directory.
        var contentRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        Directory.CreateDirectory(_contentRootPath);
        try
        {
            using var tokenStore = new TokenStore(new MockDataProtector(),
                new FakeNodeDataDirectory(_contentRootPath),
                NullLogger<TokenStore>.Instance);

            await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

            AssertEx.True(File.Exists(GetCredentialsPath()), "the credential must be written under the data dir.");
            AssertEx.False(File.Exists(Path.Combine(contentRoot, "worker-credentials.enc")), "no credential may land in the content root.");
        }
        finally
        {
            Directory.Delete(contentRoot, true);
        }
    }

    [Test]
    public async Task GetAccessTokenAsync_AfterStore_ReturnsStoredToken()
    {
        using var tokenStore = CreateTokenStore();
        var response = PairClientResponseBuilder.Valid().WithToken(CreateJwt(DateTimeOffset.UtcNow.AddDays(2))).Build();

        await tokenStore.StoreTokensAsync(response);

        AssertEx.Equal(response.AccessToken, await tokenStore.GetAccessTokenAsync());
    }

    [Test]
    public async Task GetClientNodeIdAsync_AfterStore_ReturnsStoredIdentifier()
    {
        using var tokenStore = CreateTokenStore();
        var clientNodeId = Guid.NewGuid();
        var response = PairClientResponseBuilder.Valid().WithClientNodeId(clientNodeId).Build();

        await tokenStore.StoreTokensAsync(response);

        AssertEx.Equal(clientNodeId, await tokenStore.GetClientNodeIdAsync());
    }

    [Test]
    public async Task IsTokenExpired_WhenExpiryIsPast_ReturnsTrue()
    {
        using var tokenStore = CreateTokenStore();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().WithToken(CreateJwt(expiresAt)).WithExpiresAt(expiresAt).Build());

        AssertEx.True(tokenStore.IsTokenExpired);
        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
    }

    [Test]
    public async Task IsTokenExpiringSoon_WhenExpiryWithinThreshold_ReturnsTrue()
    {
        using var tokenStore = CreateTokenStore();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(12);

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().WithToken(CreateJwt(expiresAt)).WithExpiresAt(expiresAt).Build());

        AssertEx.True(tokenStore.IsTokenExpiringSoon);
    }

    [Test]
    public async Task ClearTokensAsync_WhenTokensExist_RemovesState()
    {
        using var tokenStore = CreateTokenStore();
        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

        await tokenStore.ClearTokensAsync();

        AssertEx.False(tokenStore.IsPaired);
        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
        AssertEx.Null(await tokenStore.GetClientNodeIdAsync());
    }

    [Test]
    public async Task StoreTokensAsync_RaisesTokensChangedEventOnce()
    {
        using var tokenStore = CreateTokenStore();
        var eventCount = 0;
        tokenStore.TokensChanged += (_, _) => eventCount++;

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

        AssertEx.Equal(1, eventCount);
    }

    [Test]
    public async Task HandleKeyRotationAsync_WhenDecryptionFails_ClearsTokens()
    {
        var protector = Substitute.For<IDataProtector>();
        protector.CreateProtector(Arg.Any<string>()).Returns(protector);
        protector.Unprotect(Arg.Any<byte[]>()).Returns(_ => throw new CryptographicException("boom"));

        using var tokenStore = CreateTokenStore(protector);
        await File.WriteAllBytesAsync(GetCredentialsPath(), [1, 2, 3]);

        await tokenStore.HandleKeyRotationAsync();

        AssertEx.False(tokenStore.IsPaired);
        AssertEx.False(File.Exists(GetCredentialsPath()));
    }

    [Test]
    public async Task GetAccessTokenAsync_WhenCredentialFileIsCorrupted_ReturnsNull()
    {
        Directory.CreateDirectory(_contentRootPath);
        await File.WriteAllTextAsync(GetCredentialsPath(), "not-json");
        using var tokenStore = CreateTokenStore();

        AssertEx.Null(await tokenStore.GetAccessTokenAsync());
    }

    [Test]
    public void Dispose_WhenCalled_DoesNotThrow()
    {
        var tokenStore = CreateTokenStore();

        tokenStore.Dispose();
    }

    [Test]
    public async Task StoreTokensAsync_WhenJwtIsMalformed_UsesResponseExpiry()
    {
        using var tokenStore = CreateTokenStore();
        var expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().WithToken("not-a-jwt").WithExpiresAt(expiresAt).Build());

        AssertEx.Equal(expiresAt, tokenStore.TokenExpiresAt);
    }

    [Test]
    public async Task StoreTokensAsync_WhenMetadataProvided_PersistsBindingMetadata()
    {
        using var tokenStore = CreateTokenStore();

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build(), new TokenStoreMetadata
        {
            BindingMethod = "device-code",
            AutoConnectOnStart = false,
            LastKnownNodeName = "worker-a"
        });

        AssertEx.Equal("device-code", tokenStore.BindingMethod);
        AssertEx.False(tokenStore.AutoConnectOnStart);
        AssertEx.Equal("worker-a", tokenStore.LastKnownNodeName);
    }

    [Test]
    public async Task StoreTokensAsync_WhenMetadataOmitted_DefaultsAutoConnectOnStartFalse()
    {
        using var tokenStore = CreateTokenStore();

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

        AssertEx.False(tokenStore.AutoConnectOnStart);
        AssertEx.Equal("pairing-token", tokenStore.BindingMethod);
    }

    [Test]
    public async Task StoreTokensAsync_WhenMetadataOmitted_PreservesExistingAutoConnectPreference()
    {
        using var tokenStore = CreateTokenStore();
        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build(), new TokenStoreMetadata
        {
            AutoConnectOnStart = true
        });

        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

        AssertEx.True(tokenStore.AutoConnectOnStart);
    }

    [Test]
    public async Task SetAutoConnectOnStartAsync_WhenPaired_PersistsPreference()
    {
        using var tokenStore = CreateTokenStore();
        await tokenStore.StoreTokensAsync(PairClientResponseBuilder.Valid().Build());

        await tokenStore.SetAutoConnectOnStartAsync(false);

        AssertEx.False(tokenStore.AutoConnectOnStart);
    }

    private TokenStore CreateTokenStore(IDataProtectionProvider? dataProtectionProvider = null)
    {
        Directory.CreateDirectory(_contentRootPath);

        return new TokenStore(dataProtectionProvider ?? new MockDataProtector(),
            new FakeNodeDataDirectory(_contentRootPath),
            NullLogger<TokenStore>.Instance);
    }

    private string GetCredentialsPath()
    {
        return Path.Combine(_contentRootPath, "worker-credentials.enc");
    }

    private static string CreateJwt(DateTimeOffset expiresAt)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\"}");
        var payload = Base64UrlEncode($"{{\"exp\":{expiresAt.ToUnixTimeSeconds()}}}");
        return $"{header}.{payload}.";
    }

    private static string Base64UrlEncode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
                      .TrimEnd('=')
                      .Replace('+', '-')
                      .Replace('/', '_');
    }
}
