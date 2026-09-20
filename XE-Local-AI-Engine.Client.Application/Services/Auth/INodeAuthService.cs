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
    ///     Resets the single administrator account's password WITHOUT requiring the current one, then revokes every
    ///     active refresh token and clears any lockout.
    /// </summary>
    /// <remarks>
    ///     This is the "forgot password" recovery path: it is exposed only to the local, operator-run CLI (see
    ///     <c>--reset-admin-password</c> in Program.cs), never over the loopback HTTP surface, because the trust
    ///     boundary is the machine itself. Fails when no administrator account exists yet.
    /// </remarks>
    Task<NodePasswordChangeResult> ResetAdminPasswordAsync(string newPassword, CancellationToken cancellationToken);
}

public sealed class NodeAuthStatus
{
    public required bool SetupRequired { get; init; }

    public required bool Authenticated { get; init; }
}

/// <summary>A token-issuing outcome.</summary>
/// <remarks>
///     <see cref="LockedOutRetryAfterSeconds" /> is set only on a login that Identity refused because the account is
///     locked out, and carries the whole seconds still left on that lockout (at least one). Every other failure leaves
///     it <c>null</c>, so the transport cannot accidentally tell a wrong password apart from a locked account.
/// </remarks>
public sealed class NodeAuthTokenResult
{
    public required bool Succeeded { get; init; }

    public required string? AccessToken { get; init; }

    public required DateTime? AccessTokenExpiresAtUtc { get; init; }

    public required string? RefreshToken { get; init; }

    public required DateTime? RefreshTokenExpiresAtUtc { get; init; }

    public int? LockedOutRetryAfterSeconds { get; init; }
}

public sealed class NodeSetupResult
{
    public required bool Succeeded { get; init; }

    public required bool AlreadyInitialized { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }
}

public sealed class NodePasswordChangeResult
{
    public required bool Succeeded { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }
}

