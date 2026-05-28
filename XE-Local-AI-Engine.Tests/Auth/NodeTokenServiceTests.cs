namespace XE_Local_AI_Engine.Tests.Auth;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeTokenServiceTests
{
    [Test]
    public async Task CreateAccessToken_WhenCalled_IncludesExpectedClaimsAndExpiry()
    {
        await Task.CompletedTask;

        var key = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var issuedAt = new DateTimeOffset(2026, 5, 25, 8, 0, 0, TimeSpan.Zero);
        using var keyProvider = new FixedNodeJwtKeyProvider(key);
        var service = new NodeTokenService(keyProvider,
            Options.Create(new NodeAuthOptions
            {
                Jwt = new NodeJwtOptions
                {
                    Issuer = "issuer",
                    Audience = "audience",
                    AccessTokenMinutes = 15
                }
            }),
            new FixedTimeProvider(issuedAt));

        var user = new NodeUser
        {
            Id = "user-1",
            UserName = "admin@example.test",
            Email = "admin@example.test"
        };

        var (accessToken, expiresAtUtc) = service.CreateAccessToken(user, [NodeAuthorizationPolicies.AdminRole]);
        var token = new JsonWebTokenHandler().ReadJsonWebToken(accessToken);

        AssertEx.Equal(issuedAt.AddMinutes(15).UtcDateTime, expiresAtUtc);
        AssertEx.Equal("issuer", token.Issuer);
        AssertEx.Contains(token.Audiences, "audience");
        AssertEx.ContainsSingle(token.Claims, claim => claim.Type == JwtRegisteredClaimNames.Sub && claim.Value == "user-1");
        AssertEx.ContainsSingle(token.Claims, claim => claim.Type == JwtRegisteredClaimNames.Name && claim.Value == "admin@example.test");
        AssertEx.ContainsSingle(token.Claims, claim => claim.Type == NodeAuthorizationPolicies.RoleClaimType && claim.Value == NodeAuthorizationPolicies.AdminRole);
        AssertEx.ContainsSingle(token.Claims, claim => claim.Type == JwtRegisteredClaimNames.Jti && !string.IsNullOrWhiteSpace(claim.Value));
    }

    [Test]
    public async Task RefreshTokenHelpers_WhenCalled_CreateOpaqueTokenAndStableHash()
    {
        await Task.CompletedTask;

        using var keyProvider = new FixedNodeJwtKeyProvider(new byte[32]);
        var service = new NodeTokenService(keyProvider,
            Options.Create(new NodeAuthOptions()),
            TimeProvider.System);

        var raw = service.CreateRefreshTokenRaw();
        var rawAgain = service.CreateRefreshTokenRaw();
        var decoded = Convert.FromBase64String(raw);
        var expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

        AssertEx.Equal(64, decoded.Length);
        AssertEx.NotEqual(raw, rawAgain);
        AssertEx.Equal(expectedHash, service.HashRefreshToken(raw));
        AssertEx.Equal(expectedHash, service.HashRefreshToken(raw));
    }

    [Test]
    public async Task NodeJwtKeyProvider_WhenInputsMatch_DerivesStableKeySeparateFromSqliteKey()
    {
        await Task.CompletedTask;

        var operatorSecret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        const string nodeName = "worker-node-alpha";
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                [NodeOperatorSecretProvider.EnvVarName] = Convert.ToBase64String(operatorSecret)
                            })
                            .Build();

        using var firstJwtKeyProvider = new NodeJwtKeyProvider(Options.Create(new WorkerNodeOptions
            {
                NodeName = nodeName
            }),
            new NodeOperatorSecretProvider(configuration));
        using var secondJwtKeyProvider = new NodeJwtKeyProvider(Options.Create(new WorkerNodeOptions
            {
                NodeName = nodeName
            }),
            new NodeOperatorSecretProvider(configuration));
        using var sqliteKeyHolder = new NodeSqliteKeyHolder(Options.Create(new WorkerNodeOptions
            {
                NodeName = nodeName
            }),
            new NodeOperatorSecretProvider(configuration));

        AssertEx.True(firstJwtKeyProvider.SigningKey.Span.SequenceEqual(secondJwtKeyProvider.SigningKey.Span), "JWT key derivation should be stable for the same operator secret and node name.");
        AssertEx.False(firstJwtKeyProvider.SigningKey.Span.SequenceEqual(sqliteKeyHolder.Key.Span), "JWT and SQLite keys must use separate HKDF info strings.");
    }

    [Test]
    public async Task NodeJwtKeyProvider_WhenDisposed_ThrowsOnSubsequentAccess()
    {
        await Task.CompletedTask;

        var operatorSecret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                [NodeOperatorSecretProvider.EnvVarName] = Convert.ToBase64String(operatorSecret)
                            })
                            .Build();
        var jwtKeyProvider = new NodeJwtKeyProvider(Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker-node-alpha"
            }),
            new NodeOperatorSecretProvider(configuration));

        _ = jwtKeyProvider.SigningKey.Span[0];
        jwtKeyProvider.Dispose();

        try
        {
            _ = jwtKeyProvider.SigningKey;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new AssertionException("Expected disposed JWT key provider to throw on subsequent access.");
    }

    private sealed class FixedNodeJwtKeyProvider(byte[] key) : INodeJwtKeyProvider
    {
        public ReadOnlyMemory<byte> SigningKey => key;

        public void Dispose()
        {
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }
}
