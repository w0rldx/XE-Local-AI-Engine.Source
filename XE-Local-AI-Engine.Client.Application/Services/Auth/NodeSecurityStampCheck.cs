namespace XE_Local_AI_Engine.Client.Services.Auth;

using Microsoft.AspNetCore.Identity;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The per-request revocation check behind every validated access token. Kept apart from <see cref="INodeAuthService" />
///     so the JWT events resolve only <see cref="UserManager{TUser}" />, not the sign-in and token machinery.
/// </summary>
public sealed class NodeSecurityStampCheck
{
    private readonly UserManager<NodeUser> _userManager;

    public NodeSecurityStampCheck(UserManager<NodeUser> userManager)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        _userManager = userManager;
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
}
