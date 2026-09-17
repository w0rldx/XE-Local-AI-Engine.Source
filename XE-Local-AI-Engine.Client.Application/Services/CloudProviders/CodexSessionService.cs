namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Contracts;
using XE_Local_AI_Engine.Providers.CodexOAuth.Options;

/// <summary>
///     The Codex OAuth session as the Operator endpoints see it: presence, the non-secret account id, the
///     access-token expiry and whether a browser login is in flight. Carries no token material.
/// </summary>
/// <param name="SignedIn">True only while a stored session's access token is still valid (skew-adjusted).</param>
/// <param name="AccountId">Non-secret ChatGPT account id of the stored session, or <see langword="null" />.</param>
/// <param name="ExpiresAtUtc">Absolute UTC expiry of the stored session's access token, or <see langword="null" />.</param>
/// <param name="LoginPending">True while a loopback PKCE login is still exchanging in the background.</param>
public sealed record CodexSessionStatus(bool SignedIn, string? AccountId, DateTimeOffset? ExpiresAtUtc, bool LoginPending);

/// <summary>
///     Owns the Codex OAuth session lifecycle behind the <c>cloud/codex/*</c> Operator endpoints: start a login,
///     report the session/login state, and sign out. The expiry decision and the post-logout cache invalidation live
///     here rather than in the endpoints, so the HTTP edge only maps this type onto its response DTOs.
/// </summary>
public sealed class CodexSessionService(
    ICodexTokenStore tokenStore,
    ICodexLoginCoordinator loginCoordinator,
    IActiveCloudChatClientFactory activeCloudFactory,
    IOptions<CodexOptions> codexOptions,
    TimeProvider timeProvider)
{
    private readonly IActiveCloudChatClientFactory _activeCloudFactory =
        activeCloudFactory ?? throw new ArgumentNullException(nameof(activeCloudFactory));

    private readonly CodexOptions _codexOptions =
        (codexOptions ?? throw new ArgumentNullException(nameof(codexOptions))).Value;

    private readonly ICodexLoginCoordinator _loginCoordinator =
        loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ICodexTokenStore _tokenStore =
        tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));

    /// <summary>
    ///     Starts a loopback PKCE login, superseding any in-flight attempt, and returns the authorize URL the
    ///     operator opens in a browser. The token exchange completes in the background.
    /// </summary>
    public Uri StartLogin()
    {
        return _loginCoordinator.Start();
    }

    /// <summary>Reports the current session and login state. Returns no token material.</summary>
    public async Task<CodexSessionStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var session = await _tokenStore.LoadAsync(cancellationToken);
        var loginPending = _loginCoordinator.GetStatus().State == CodexLoginState.Pending;

        return session is null
            ? new CodexSessionStatus(false, null, null, loginPending)
            // Signed-in iff the access token is still valid (skew-adjusted); an expired session reports
            // SignedIn=false while keeping AccountId/ExpiresAtUtc so the UI can prompt re-authentication.
            : new CodexSessionStatus(!session.IsExpired(_codexOptions.ExpirySkew, _timeProvider.GetUtcNow()),
                session.AccountId,
                session.ExpiresUtc,
                loginPending);
    }

    /// <summary>Clears the stored Codex OAuth session so the next chat send routes back to Azure-or-local.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _tokenStore.ClearAsync(cancellationToken);

        // Invalidate the selector's snapshot so the very next send reverts to Azure/local without waiting for the TTL.
        _activeCloudFactory.InvalidateSelectionCache();
    }
}
