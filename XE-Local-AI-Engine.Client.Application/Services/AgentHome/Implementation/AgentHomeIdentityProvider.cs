namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     AgentHome gateway <see cref="IAgentHomeIdentityProvider" />. The node id comes from <see cref="ITokenStore" /> (falling
///     back to the deterministic local-loopback node id when the worker is not yet paired). The owner id is the user
///     subject decoded from the worker's stored access token — the server mints it with the user id in both the
///     <c>sub</c> and <see cref="ClaimTypes.NameIdentifier" /> claims (distinct from the <c>nodeId</c> claim), so the
///     owner boundary is the authenticated user, not the node. When no token is present (unpaired local-loopback) the
///     owner falls back to the node id so loopback stays deterministic. The token is read claim-only without
///     re-validation: it was already validated at acquisition and this provider only reads a claim.
/// </summary>
internal sealed class AgentHomeIdentityProvider : IAgentHomeIdentityProvider
{
    private readonly ITokenStore _tokenStore;

    public AgentHomeIdentityProvider(ITokenStore tokenStore)
    {
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
    }

    public async Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var clientNodeId = await _tokenStore.GetClientNodeIdAsync().ConfigureAwait(false)
                           ?? LocalChatLoopbackDefaults.ClientNodeId;
        var nodeId = clientNodeId.ToString();

        var accessToken = await _tokenStore.GetAccessTokenAsync().ConfigureAwait(false);
        var ownerUserId = ResolveOwnerSubject(accessToken) ?? nodeId;

        return new AgentHomeOwnerIdentity(ownerUserId, nodeId);
    }

    private static string? ResolveOwnerSubject(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(accessToken))
        {
            return null;
        }

        JsonWebToken token;
        try
        {
            // Read-only decode — the token was already validated when it was acquired; we only read the subject claim.
            token = handler.ReadJsonWebToken(accessToken);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // The server sets the user id on both sub and NameIdentifier; JsonWebToken.Subject surfaces the sub claim. Fall
        // back to NameIdentifier, then to a blank → node-scoped owner.
        var subject = !string.IsNullOrWhiteSpace(token.Subject)
            ? token.Subject
            : token.Claims.FirstOrDefault(claim =>
                string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal))?.Value;

        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }
}
