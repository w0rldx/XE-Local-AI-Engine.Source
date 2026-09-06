namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;

public interface INodeAuthService
{
    Task<NodeAuthStatus> GetStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<NodeSetupResult> SetupAsync(string email, string password, CancellationToken cancellationToken);

    Task<NodeAuthTokenResult> LoginAsync(string? email, string password, CancellationToken cancellationToken);

    Task<NodeAuthTokenResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken);

    Task RevokeRefreshTokensAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<NodePasswordChangeResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    ///     Resets the single administrator account's password WITHOUT requiring the current one, then revokes every active
    ///     refresh token and clears any lockout. This is the "forgot password" recovery path: it is exposed only to the
    ///     local, operator-run CLI (see <c>--reset-admin-password</c> in Program.cs), never over the loopback HTTP surface,
    ///     because the trust boundary is the machine itself. Fails when no administrator account exists yet.
    /// </summary>
    Task<NodePasswordChangeResult> ResetAdminPasswordAsync(string newPassword, CancellationToken cancellationToken);

    Task<NodeCurrentUser?> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed record NodeAuthStatus(bool SetupRequired, bool Authenticated);

/// <summary>
///     A token-issuing outcome. <paramref name="LockedOutRetryAfterSeconds" /> is set only on a login that Identity
///     refused because the account is locked out, and carries the whole seconds still left on that lockout (at least
///     one). Every other failure leaves it <c>null</c>, so the transport cannot accidentally tell a wrong password
///     apart from a locked account.
/// </summary>
public sealed record NodeAuthTokenResult(
    bool Succeeded,
    string? AccessToken,
    DateTime? AccessTokenExpiresAtUtc,
    string? RefreshToken,
    DateTime? RefreshTokenExpiresAtUtc,
    int? LockedOutRetryAfterSeconds = null);

public sealed record NodeSetupResult(bool Succeeded, bool AlreadyInitialized, IReadOnlyList<string> Errors);

public sealed record NodePasswordChangeResult(bool Succeeded, IReadOnlyList<string> Errors);

public sealed record NodeCurrentUser(string UserName, IReadOnlyList<string> Roles);
