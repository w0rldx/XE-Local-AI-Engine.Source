namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Vault;

public interface INodeAuthService
{
    Task<NodeAuthStatus> GetStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<NodeSetupResult> SetupAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>Signs in and starts a new session.</summary>
    /// <param name="clientLabel">The request's User-Agent, stored truncated as the session's label; may be null.</param>
    Task<NodeAuthTokenResult> LoginAsync(string? email, string password, string? clientLabel, CancellationToken cancellationToken);

    /// <summary>Rotates a refresh token inside its session; a token issued before sessions existed gets a new session.</summary>
    /// <param name="clientLabel">The request's User-Agent, used only when a pre-session token gets its session.</param>
    Task<NodeAuthTokenResult> RefreshAsync(string? refreshToken, string? clientLabel, CancellationToken cancellationToken);

    /// <summary>
    ///     Issues a new token chain for the single admin after the vault unlock proved the admin password (ADR 0018
    ///     decision 4). The caller has already consumed the one-time unlock ticket; fails when no admin exists.
    /// </summary>
    Task<NodeAuthTokenResult> IssueSessionAfterVaultUnlockAsync(string? clientLabel, CancellationToken cancellationToken);

    /// <summary>
    ///     Logout: ends the caller's session (the access token's <c>sid</c>, else the presented refresh token's session) and
    ///     its refresh tokens. Other sessions stay signed in.
    /// </summary>
    Task EndCurrentSessionAsync(ClaimsPrincipal principal, string? refreshToken, CancellationToken cancellationToken);

    /// <summary>The caller's live sessions, most recently used first.</summary>
    Task<IReadOnlyList<NodeAuthSession>> ListSessionsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    /// <summary>
    ///     Ends one of the caller's own sessions. Throws <see cref="NodeAuthSessionNotFoundException" /> when the caller
    ///     owns no live session with that id.
    /// </summary>
    Task RevokeSessionAsync(ClaimsPrincipal principal, string sessionId, CancellationToken cancellationToken);

    /// <summary>"Sign out everywhere": ends every session of the caller, the current one included, and rotates the security stamp.</summary>
    Task RevokeAllSessionsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<NodePasswordChangeResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <summary>
    ///     Resets the single administrator account's password WITHOUT requiring the current one, then revokes every
    ///     active refresh token and clears any lockout.
    /// </summary>
    /// <remarks>
    ///     This is the "forgot password" recovery path: it is exposed only to the local, operator-run CLI (see
    ///     <c>--reset-admin-password</c> in Program.cs), never over the loopback HTTP surface, because the trust
    ///     boundary is the machine itself. Fails when no administrator account exists yet. When a v2 <c>node.key</c>
    ///     exists the machine is no longer enough: <paramref name="recoveryCode" /> must unwrap the vault, which is then
    ///     re-wrapped under <paramref name="newPassword" /> before Identity is touched.
    /// </remarks>
    /// <param name="newRecoveryCode">The rotated code when the caller already showed it; <see langword="null" /> mints one.</param>
    /// <returns>The outcome; after a recovery-code reset, <see cref="NodePasswordChangeResult.RecoveryCode" /> is the rotated code.</returns>
    Task<NodePasswordChangeResult> ResetAdminPasswordAsync(string newPassword, string? recoveryCode, string? newRecoveryCode, CancellationToken cancellationToken);

    /// <summary>
    ///     Wraps a legacy (pre-vault) <c>node.key</c> under the signed-in admin's password once Identity has verified
    ///     it, returning the one-time recovery code. Valid only while the vault is <see cref="VaultState.Pending" />
    ///     and setup is complete.
    /// </summary>
    Task<NodeVaultConfirmResult> ConfirmLegacyVaultAsync(ClaimsPrincipal principal, string password, CancellationToken cancellationToken);
}

public sealed class NodeAuthStatus
{
    public required bool SetupRequired { get; init; }

    public required bool Authenticated { get; init; }

    /// <summary>The node vault state; <see cref="VaultState.External" /> when the secret is in operator custody.</summary>
    public VaultState Vault { get; init; } = VaultState.External;
}

/// <summary>A token-issuing outcome: the whole pair, or a failure that carries no part of one.</summary>
/// <remarks>
///     <see cref="Failed.LockedOutRetryAfterSeconds" /> is set only on a login that Identity refused because the account
///     is locked out, and carries the whole seconds still left on that lockout (at least one). Every other failure
///     leaves it <c>null</c>, so the transport cannot accidentally tell a wrong password apart from a locked account.
/// </remarks>
public abstract record NodeAuthTokenResult
{
    public sealed record Issued : NodeAuthTokenResult
    {
        public required string AccessToken { get; init; }

        public required DateTime AccessTokenExpiresAtUtc { get; init; }

        public required string RefreshToken { get; init; }

        public required DateTime RefreshTokenExpiresAtUtc { get; init; }
    }

    public sealed record Failed : NodeAuthTokenResult
    {
        public int? LockedOutRetryAfterSeconds { get; init; }
    }
}

public sealed class NodeSetupResult
{
    public required bool Succeeded { get; init; }

    public required bool AlreadyInitialized { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>The one-time vault recovery code; <c>null</c> when the vault is not managed (operator-custody secret).</summary>
    public string? RecoveryCode { get; init; }
}

public sealed class NodeVaultConfirmResult
{
    public required bool Succeeded { get; init; }

    /// <summary>The vault is not waiting for a confirmation (already wrapped, external custody, or setup incomplete).</summary>
    public bool NotPending { get; init; }

    public string? RecoveryCode { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class NodePasswordChangeResult
{
    public required bool Succeeded { get; init; }

    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>The rotated vault recovery code after a successful recovery reset; <see langword="null" /> otherwise.</summary>
    public string? RecoveryCode { get; init; }
}

/// <summary>
///     Thrown when the caller names a session it does not own: another user's session and an unknown id are the same
///     answer. <c>ResourceNotFoundExceptionHandler</c> answers it with the bodyless 404.
/// </summary>
public sealed class NodeAuthSessionNotFoundException : ResourceNotFoundException
{
    public NodeAuthSessionNotFoundException() : base("No session of the caller has that id.")
    {
    }
}
