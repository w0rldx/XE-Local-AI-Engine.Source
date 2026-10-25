namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The per-request revocation checks behind every validated access token: the security stamp and the sign-in session.
///     Kept apart from <see cref="INodeAuthService" /> so the JWT events resolve no sign-in or token machinery.
/// </summary>
public sealed class NodeSecurityStampCheck
{
    private readonly INodeIdentityStore _identity;
    private readonly UserManager<NodeUser> _userManager;

    public NodeSecurityStampCheck(UserManager<NodeUser> userManager, INodeIdentityStore identity)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(identity);
        _userManager = userManager;
        _identity = identity;
    }

    /// <summary>
    ///     Why the validated token behind <paramref name="principal" /> is no longer honoured, or <see langword="null" /> when
    ///     it still is. One check for the JWT events (every request and hub handshake) and the hub filter (every invocation).
    /// </summary>
    public async Task<string?> FindRevocationAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var userId = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return null;
        }

        // Fail CLOSED when the token carries no stamp: every token minted for a persisted user binds one, so an unstamped
        // but validly-signed token is a legacy token that must not outlive a reset, or a forgery. Never a bypass.
        var tokenStamp = principal.FindFirst(NodeAuthorizationPolicies.SecurityStampClaimType)?.Value;
        if (string.IsNullOrEmpty(tokenStamp))
        {
            return "Access token is missing its security stamp.";
        }

        if (!await IsCurrentAsync(userId, tokenStamp))
        {
            return "Access token security stamp is stale.";
        }

        // A token minted before sessions existed carries no sid and lives out its short lifetime; one that names a session
        // dies with it (logout, the sessions list, sign out everywhere).
        var sessionId = principal.FindFirst(JwtRegisteredClaimNames.Sid)?.Value;
        return !string.IsNullOrEmpty(sessionId) && !await IsSessionLiveAsync(userId, sessionId, cancellationToken)
            ? "Access token session has ended."
            : null;
    }

    /// <summary>
    ///     Whether <paramref name="tokenStamp" /> still matches the persisted security stamp of user
    ///     <paramref name="userId" />.
    /// </summary>
    /// <remarks>
    ///     A subject with no persisted row answers <c>true</c>: the stamp is a revocation signal, not an existence
    ///     check, so the base stateless-JWT posture is preserved and each endpoint resolves the user itself.
    /// </remarks>
    public async Task<bool> IsCurrentAsync(string userId, string tokenStamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(tokenStamp);

        var user = await _userManager.FindByIdAsync(userId);
        return user is null
               || string.Equals(await _userManager.GetSecurityStampAsync(user), tokenStamp, StringComparison.Ordinal);
    }

    /// <summary>Whether the token's <c>sid</c> names a live session of <paramref name="userId" />; a missing or revoked one fails.</summary>
    public Task<bool> IsSessionLiveAsync(string userId, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        return _identity.IsSessionLiveAsync(sessionId, userId, cancellationToken);
    }
}
