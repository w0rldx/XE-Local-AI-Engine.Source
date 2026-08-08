namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the persistence-availability fallback decorator: a working persisted-cache credential is used as-is; a
///     <see cref="CredentialUnavailableException" /> triggers exactly one rebuild without persistence options
///     (never unencrypted-on-disk), and the fallback decision sticks for later calls.
/// </summary>
public sealed class EntraPersistenceFallbackCredentialTests
{
    [Test]
    public void GetToken_WhenInnerCredentialSucceeds_ReturnsItsTokenWithoutFallingBack()
    {
        var buildCallCount = 0;
        var credential = new EntraPersistenceFallbackCredential(options =>
            {
                buildCallCount++;
                AssertEx.NotNull(options);
                return new StubTokenCredential("primary-token");
            },
            new TokenCachePersistenceOptions
            {
                Name = "test-cache"
            },
            NullLogger.Instance);

        var token = credential.GetToken(new TokenRequestContext(["scope"]), CancellationToken.None);

        AssertEx.Equal("primary-token", token.Token);
        AssertEx.Equal(1, buildCallCount);
    }

    [Test]
    public void GetToken_WhenInnerCredentialThrowsCredentialUnavailable_FallsBackToInMemoryCredential()
    {
        var receivedOptions = new List<TokenCachePersistenceOptions?>();
        var credential = new EntraPersistenceFallbackCredential(options =>
            {
                receivedOptions.Add(options);
                return options is null
                    ? new StubTokenCredential("fallback-token")
                    : new ThrowingTokenCredential();
            },
            new TokenCachePersistenceOptions
            {
                Name = "test-cache"
            },
            NullLogger.Instance);

        var token = credential.GetToken(new TokenRequestContext(["scope"]), CancellationToken.None);

        AssertEx.Equal("fallback-token", token.Token);
        AssertEx.Equal(2, receivedOptions.Count);
        AssertEx.NotNull(receivedOptions[0]);
        AssertEx.Null(receivedOptions[1]);
    }

    [Test]
    public void GetToken_AfterFallingBackOnce_DoesNotRebuildOnSubsequentCalls()
    {
        var buildCallCount = 0;
        var credential = new EntraPersistenceFallbackCredential(options =>
            {
                buildCallCount++;
                return buildCallCount == 1 ? new ThrowingTokenCredential() : new StubTokenCredential("fallback-token");
            },
            new TokenCachePersistenceOptions
            {
                Name = "test-cache"
            },
            NullLogger.Instance);

        credential.GetToken(new TokenRequestContext(["scope"]), CancellationToken.None);
        var second = credential.GetToken(new TokenRequestContext(["scope"]), CancellationToken.None);

        AssertEx.Equal("fallback-token", second.Token);
        AssertEx.Equal(2, buildCallCount);
    }

    [Test]
    public async Task GetTokenAsync_WhenInnerCredentialThrowsCredentialUnavailable_FallsBackToInMemoryCredential()
    {
        var credential = new EntraPersistenceFallbackCredential(options => options is null ? new StubTokenCredential("fallback-token") : new ThrowingTokenCredential(),
            new TokenCachePersistenceOptions
            {
                Name = "test-cache"
            },
            NullLogger.Instance);

        var token = await credential.GetTokenAsync(new TokenRequestContext(["scope"]), CancellationToken.None);

        AssertEx.Equal("fallback-token", token.Token);
    }

    private sealed class StubTokenCredential(string tokenValue) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken(tokenValue, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }

    private sealed class ThrowingTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            throw new CredentialUnavailableException("Encrypted token-cache persistence is unavailable on this platform.");
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            throw new CredentialUnavailableException("Encrypted token-cache persistence is unavailable on this platform.");
        }
    }
}
