namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Services.Vault;

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
