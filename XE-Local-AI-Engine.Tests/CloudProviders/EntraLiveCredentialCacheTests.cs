namespace XE_Local_AI_Engine.Tests.CloudProviders;

using Azure.Core;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the single-slot live credential cache: a miss before anything is stored, a hit only for the exact key
///     it was stored under, and last-write-wins on overwrite (matches the single-connection cloud-credential model).
/// </summary>
public sealed class EntraLiveCredentialCacheTests
{
    [Test]
    public void TryGet_WhenNothingStored_ReturnsNull()
    {
        var cache = new EntraLiveCredentialCache();

        AssertEx.Null(cache.TryGet("tenant|client|scope"));
    }

    [Test]
    public void TryGet_WhenStoredUnderMatchingKey_ReturnsTheCredential()
    {
        var cache = new EntraLiveCredentialCache();
        var credential = new StubTokenCredential();

        cache.Store("tenant|client|scope", credential);

        AssertEx.True(ReferenceEquals(credential, cache.TryGet("tenant|client|scope")));
    }

    [Test]
    public void TryGet_WhenKeyDoesNotMatchStoredKey_ReturnsNull()
    {
        var cache = new EntraLiveCredentialCache();
        cache.Store("tenant-a|client|scope", new StubTokenCredential());

        AssertEx.Null(cache.TryGet("tenant-b|client|scope"));
    }

    [Test]
    public void Store_WhenCalledAgainWithADifferentKey_ReplacesTheSingleSlot()
    {
        // A settings change (or a fresh sign-in) simply overwrites the single slot — the old entry is dropped, not
        // accumulated, matching the single Azure Foundry connection model.
        var cache = new EntraLiveCredentialCache();
        var first = new StubTokenCredential();
        var second = new StubTokenCredential();
        cache.Store("tenant-a|client|scope", first);

        cache.Store("tenant-b|client|scope", second);

        AssertEx.Null(cache.TryGet("tenant-a|client|scope"));
        AssertEx.True(ReferenceEquals(second, cache.TryGet("tenant-b|client|scope")));
    }

    [Test]
    public void CreateKey_WhenCalledWithTheSameFields_ProducesTheSameKey()
    {
        var first = EntraDeviceCodeCredentialCacheKey.Create("tenant-id", "client-id", "api://backend/.default");
        var second = EntraDeviceCodeCredentialCacheKey.Create("tenant-id", "client-id", "api://backend/.default");

        AssertEx.Equal(first, second);
    }

    [Test]
    public void CreateKey_WhenAnyFieldDiffers_ProducesADifferentKey()
    {
        var baseline = EntraDeviceCodeCredentialCacheKey.Create("tenant-id", "client-id", "api://backend/.default");
        var differentTenant = EntraDeviceCodeCredentialCacheKey.Create("other-tenant", "client-id", "api://backend/.default");

        AssertEx.NotEqual(baseline, differentTenant);
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(GetToken(requestContext, cancellationToken));
        }
    }
}
