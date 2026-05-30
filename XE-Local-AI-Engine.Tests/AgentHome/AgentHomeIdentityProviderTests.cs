namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker I owner-subject derivation (Decision 6): the owner id is the user subject decoded from the worker access
///     token (the server mints it in both <c>sub</c> and <see cref="ClaimTypes.NameIdentifier" />), while the node id
///     stays the persisted client node id. With no token (unpaired loopback) the owner falls back to the node id.
/// </summary>
public sealed class AgentHomeIdentityProviderTests
{
    private static readonly Guid NodeId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Test]
    public async Task GetAsync_WhenAccessTokenCarriesSubject_OwnerIsTokenSubject()
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetClientNodeIdAsync().Returns(Task.FromResult<Guid?>(NodeId));
        tokenStore.GetAccessTokenAsync().Returns(Task.FromResult<string?>(CreateToken("user-subject-7")));

        var provider = new AgentHomeIdentityProvider(tokenStore);

        var identity = await provider.GetAsync();

        AssertEx.Equal("user-subject-7", identity.OwnerUserId);
        AssertEx.Equal(NodeId.ToString(), identity.NodeId);
    }

    [Test]
    public async Task GetAsync_WhenNoAccessToken_OwnerFallsBackToNodeId()
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetClientNodeIdAsync().Returns(Task.FromResult<Guid?>(NodeId));
        tokenStore.GetAccessTokenAsync().Returns(Task.FromResult<string?>(null));

        var provider = new AgentHomeIdentityProvider(tokenStore);

        var identity = await provider.GetAsync();

        AssertEx.Equal(NodeId.ToString(), identity.OwnerUserId);
        AssertEx.Equal(NodeId.ToString(), identity.NodeId);
    }

    [Test]
    public async Task GetAsync_WhenAccessTokenIsNotAJwt_OwnerFallsBackToNodeId()
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetClientNodeIdAsync().Returns(Task.FromResult<Guid?>(NodeId));
        tokenStore.GetAccessTokenAsync().Returns(Task.FromResult<string?>("not-a-jwt"));

        var provider = new AgentHomeIdentityProvider(tokenStore);

        var identity = await provider.GetAsync();

        AssertEx.Equal(NodeId.ToString(), identity.OwnerUserId);
    }

    [Test]
    public async Task GetAsync_WhenNoNodeIdAndNoToken_OwnerAndNodeAreLoopbackDefault()
    {
        var tokenStore = Substitute.For<ITokenStore>();
        tokenStore.GetClientNodeIdAsync().Returns(Task.FromResult<Guid?>(null));
        tokenStore.GetAccessTokenAsync().Returns(Task.FromResult<string?>(null));

        var provider = new AgentHomeIdentityProvider(tokenStore);

        var identity = await provider.GetAsync();

        var loopback = LocalChatLoopbackDefaults.ClientNodeId.ToString();
        AssertEx.Equal(loopback, identity.NodeId);
        AssertEx.Equal(loopback, identity.OwnerUserId);
    }

    private static string CreateToken(string subject)
    {
        // Mirror the server WorkerNodeTokenService claim shape: sub + NameIdentifier carry the user id; nodeId is
        // separate. The signing key is irrelevant — the provider reads the token claim-only without validation.
        var signingKey = new SymmetricSecurityKey(new byte[32]);
        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = subject,
                [ClaimTypes.NameIdentifier] = subject,
                ["nodeId"] = NodeId.ToString()
            },
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
