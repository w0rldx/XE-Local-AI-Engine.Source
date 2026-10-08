namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Vault;

public sealed class NodeAuthService : INodeAuthService
{
    private const int MaxRotationChainHops = 16;

    /// <summary>Identity's own PasswordMismatch wording, so a vault refusal reads the same as an Identity one.</summary>
    private const string IncorrectPasswordError = "Incorrect password.";

    private const string RecoveryCodeRequiredError =
        "This node's key is protected by the admin password; a password reset requires the recovery code shown at setup.";

    private static readonly SemaphoreSlim SetupLock = new(initialCount: 1, maxCount: 1);

    // Confirm, change and reset each prove a password and then write vault and Identity in separate steps; run alone, a
    // change landing between a confirm's check and its wrap would leave node.key on the old password.
    private static readonly SemaphoreSlim PasswordLock = new(initialCount: 1, maxCount: 1);

    /// <summary>How long a refresh token that ROTATION replaced still buys a successor.</summary>
    /// <remarks>
    ///     The SPA keeps its access token in memory only, so every document load refreshes; a reload while a refresh is
    ///     already in flight, or a second tab, makes two requests present the same cookie, and single-use rotation would
    ///     answer the loser 401 — which clears the cookie and signs the operator out although nothing was compromised.
    ///     Not an option: an operator has no reason to tune it, and every second of it is a second a captured cookie
    ///     stays replayable.
    /// </remarks>
    private static readonly TimeSpan RotationGraceWindow = TimeSpan.FromSeconds(10);

    private readonly INodeIdentityStore _identity;
    private readonly ILogger<NodeAuthService> _logger;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly IOptions<NodeAuthOptions> _options;
    private readonly SignInManager<NodeUser> _signInManager;
    private readonly TimeProvider _timeProvider;
    private readonly INodeTokenService _tokenService;
    private readonly UserManager<NodeUser> _userManager;
    private readonly INodeVault _vault;

    public NodeAuthService(INodeIdentityStore identity,
        UserManager<NodeUser> userManager,
        SignInManager<NodeUser> signInManager,
        INodeTokenService tokenService,
        IOptions<NodeAuthOptions> options,
        INodeSettingsStore nodeSettingsStore,
        TimeProvider timeProvider,
        INodeVault vault,
        ILogger<NodeAuthService> logger)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _signInManager = signInManager ?? throw new ArgumentNullException(nameof(signInManager));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeAuthStatus> GetStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var hasAdminUser = await _identity.HasCompletedSetupAsync(cancellationToken);

        return new NodeAuthStatus
        {
            SetupRequired = !hasAdminUser,
            Authenticated = principal.Identity?.IsAuthenticated == true,
            Vault = _vault.State
        };
    }

    /// <summary>Creates the single administrator account and stamps the pending external-access and sandbox security profiles.</summary>
    /// <remarks>
    ///     The settings file cannot join the identity transaction, so one of the two writes has to be the orphanable one. Writing the profile
    ///     FIRST makes the orphan "profile pending, no administrator", which is inert: setup fails and is retryable, the gated services keep
    ///     waiting because there is no operator to ask, and the retry lands on the same null guard. Writing after the commit would instead
    ///     leave "administrator exists, profile null" reachable — the one state the boot backfill decides as "recommended", which would start
    ///     outbound checks the operator was never asked about.
    /// </remarks>
    public async Task<NodeSetupResult> SetupAsync(string email, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await SetupLock.WaitAsync(cancellationToken);
        try
        {
            if (await HasCompletedSetupAsync(cancellationToken))
            {
                return new NodeSetupResult
                {
                    Succeeded = false,
                    AlreadyInitialized = true,
                    Errors = []
                };
            }

            await using var transaction = await _identity.BeginSerializableTransactionAsync(cancellationToken);

            if (await HasCompletedSetupAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new NodeSetupResult
                {
                    Succeeded = false,
                    AlreadyInitialized = true,
                    Errors = []
                };
            }

            var normalizedEmail = email.Trim();
            var user = new NodeUser
            {
                Email = normalizedEmail,
                UserName = normalizedEmail,
                SetupCompleted = true,
                CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
            };

            var createResult = await _userManager.CreateAsync(user, password);
            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new NodeSetupResult
                {
                    Succeeded = false,
                    AlreadyInitialized = false,
                    Errors = ToErrorList(createResult)
                };
            }

            var roleResult = await _userManager.AddToRoleAsync(user, NodeAuthorizationPolicies.AdminRole);
            if (!roleResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new NodeSetupResult
                {
                    Succeeded = false,
                    AlreadyInitialized = false,
                    Errors = ToErrorList(roleResult)
                };
            }

            // Both profiles (ADR 0020 for the sandbox one), BEFORE the commit and only where nothing was chosen yet: the remarks say why that
            // order is safe. The token is SetupAsync's own: a cancellation here throws before the commit, so the transaction disposes unconfirmed.
            await _nodeSettingsStore.UpdateAsync(latest => latest with
                {
                    ExternalAccessProfile = latest.ExternalAccessProfile ?? StoredNodeSettings.ExternalAccessProfilePending,
                    SandboxSecurityProfile = latest.SandboxSecurityProfile ?? StoredNodeSettings.SandboxSecurityProfilePending
                },
                cancellationToken);

            // The vault wrap is the last step inside the transaction: if it throws, the transaction disposes unconfirmed and
            // no admin exists; if the commit throws after it, the file is put back so the next setup starts from Pending.
            var vaultChange = await _vault.CreateAsync(password, replaceUnlocked: true, cancellationToken);
            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch when (vaultChange is not null)
            {
                await _vault.RestoreAsync(vaultChange, CancellationToken.None);
                throw;
            }

            _logger.LogInformation("Node admin user created during first-run setup.");
            return new NodeSetupResult
            {
                Succeeded = true,
                AlreadyInitialized = false,
                Errors = [],
                RecoveryCode = vaultChange?.RecoveryCode
            };
        }
        finally
        {
            SetupLock.Release();
        }
    }

    public async Task<NodeAuthTokenResult> LoginAsync(string? email, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var user = await ResolveLoginUserAsync(email, cancellationToken);
        if (user is null)
        {
            _logger.LogWarning("Node login failed: no matching user.");
            return FailedTokenResult();
        }

        var signInResult = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!signInResult.Succeeded)
        {
            _logger.LogWarning("Node login failed for user {UserId}: {Reason}.", user.Id, GetSignInFailureReason(signInResult));

            // A locked-out login is the ONE failure reported distinguishably: otherwise an operator who mistyped five times reads "incorrect
            // password" holding the right one. It tells a caller an email exists after five attempts — accepted on a loopback-only node capped at 10 requests/minute per IP.
            return signInResult.IsLockedOut
                ? FailedTokenResult(await GetLockoutRetryAfterSecondsAsync(user))
                : FailedTokenResult();
        }

        // A new, independent rotation chain: signing in on one client never signs another out.
        return await CreateTokenResultAsync(user, _timeProvider.GetUtcNow().UtcDateTime, rotated: null, cancellationToken);
    }

    public async Task<NodeAuthTokenResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return FailedTokenResult();
        }

        var refreshTokenHash = _tokenService.HashRefreshToken(refreshToken);
        await using var transaction = await _identity.BeginSerializableTransactionAsync(cancellationToken);

        var storedToken = await _identity.FindRefreshTokenAsync(refreshTokenHash, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (storedToken is null || storedToken.ExpiresAtUtc <= now)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning("Node refresh failed: missing or expired refresh token.");
            return FailedTokenResult();
        }

        var withinRotationGrace = storedToken.RevokedAtUtc is not null
                                  && await IsWithinRotationGraceAsync(storedToken, now, cancellationToken);

        if (storedToken.RevokedAtUtc is not null && !withinRotationGrace)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning("Node refresh failed: revoked refresh token.");
            return FailedTokenResult();
        }

        var user = await _userManager.FindByIdAsync(storedToken.UserId);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning("Node refresh failed: user {UserId} not found.", storedToken.UserId);
            return FailedTokenResult();
        }

        if (withinRotationGrace)
        {
            // Not re-stamped, so replaying every few seconds cannot walk the window forward. The pair issued below is a
            // sibling of the chain's live head, not its replacement: whichever Set-Cookie a browser keeps stays live.
            _logger.LogInformation("Node refresh honoured a token that rotation replaced inside the grace window for user {UserId}.",
                storedToken.UserId);
        }

        var result = await CreateTokenResultAsync(user, now, withinRotationGrace ? null : storedToken, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task RevokeRefreshTokensAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
        {
            return;
        }

        await _identity.RevokeActiveTokensAsync(user.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    }

    public Task<NodePasswordChangeResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken) =>
        UnderPasswordLockAsync(() => ChangePasswordCoreAsync(principal, currentPassword, newPassword, cancellationToken), cancellationToken);

    public Task<NodePasswordChangeResult> ResetAdminPasswordAsync(string newPassword, string? recoveryCode, CancellationToken cancellationToken) =>
        UnderPasswordLockAsync(() => ResetAdminPasswordCoreAsync(newPassword, recoveryCode, cancellationToken), cancellationToken);

    public Task<NodeVaultConfirmResult> ConfirmLegacyVaultAsync(ClaimsPrincipal principal, string password, CancellationToken cancellationToken) =>
        UnderPasswordLockAsync(() => ConfirmLegacyVaultCoreAsync(principal, password, cancellationToken), cancellationToken);

    private static async Task<T> UnderPasswordLockAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await PasswordLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            PasswordLock.Release();
        }
    }

    private async Task<NodePasswordChangeResult> ChangePasswordCoreAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
        {
            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = ["The current session is invalid."]
            };
        }

        // Vault first (it proves the current password against node.key), then Identity; no shared transaction, so an Identity
        // failure restores the file. A crash between the two leaves Identity on the old password: recoverable with the code.
        VaultChange? vaultChange;
        try
        {
            vaultChange = await _vault.RewrapAsync(currentPassword, newPassword, cancellationToken);
        }
        catch (VaultUnlockException)
        {
            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = [IncorrectPasswordError]
            };
        }

        IdentityResult result;
        try
        {
            result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        }
        catch when (vaultChange is not null)
        {
            await _vault.RestoreAsync(vaultChange, CancellationToken.None);
            throw;
        }

        if (!result.Succeeded)
        {
            if (vaultChange is not null)
            {
                await _vault.RestoreAsync(vaultChange, CancellationToken.None);
            }

            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = ToErrorList(result)
            };
        }

        await _identity.RevokeActiveTokensAsync(user.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return new NodePasswordChangeResult
        {
            Succeeded = true,
            Errors = []
        };
    }

    private async Task<NodePasswordChangeResult> ResetAdminPasswordCoreAsync(string newPassword, string? recoveryCode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        if (_vault.State.HasWrappedKey() && string.IsNullOrWhiteSpace(recoveryCode))
        {
            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = [RecoveryCodeRequiredError]
            };
        }

        // No email on a recovery reset: resolve the single completed-setup admin, exactly as login does when the UI omits
        // the email (single-user model). Nothing to reset if first-run setup never happened.
        var user = await ResolveLoginUserAsync(email: null, cancellationToken);
        if (user is null)
        {
            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = ["No administrator account exists. Complete first-run setup before resetting the password."]
            };
        }

        // The recovery code is proven against node.key and the vault re-wrapped BEFORE Identity; a failed Identity reset puts the file back.
        VaultChange? vaultChange = null;
        if (_vault.State.HasWrappedKey())
        {
            try
            {
                vaultChange = await _vault.RewrapWithRecoveryAsync(recoveryCode!, newPassword, cancellationToken);
            }
            catch (VaultUnlockException)
            {
                return new NodePasswordChangeResult
                {
                    Succeeded = false,
                    Errors = ["The recovery code is not valid for this node."]
                };
            }
        }

        try
        {
            var result = await ResetIdentityPasswordAsync(user, newPassword, cancellationToken);
            if (!result.Succeeded && vaultChange is not null)
            {
                await _vault.RestoreAsync(vaultChange, CancellationToken.None);
            }

            return result;
        }
        catch when (vaultChange is not null)
        {
            await _vault.RestoreAsync(vaultChange, CancellationToken.None);
            throw;
        }
    }

    private async Task<NodeVaultConfirmResult> ConfirmLegacyVaultCoreAsync(ClaimsPrincipal principal, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        if (_vault.State is not VaultState.Pending || !await HasCompletedSetupAsync(cancellationToken))
        {
            return new NodeVaultConfirmResult
            {
                Succeeded = false,
                NotPending = true
            };
        }

        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
        {
            return new NodeVaultConfirmResult
            {
                Succeeded = false,
                Errors = ["The current session is invalid."]
            };
        }

        // lockoutOnFailure: this endpoint verifies the password, so it must not become an unthrottled guessing oracle beside login.
        var check = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!check.Succeeded)
        {
            return new NodeVaultConfirmResult
            {
                Succeeded = false,
                Errors = [check.IsLockedOut ? "The account is temporarily locked. Try again later." : IncorrectPasswordError]
            };
        }

        VaultChange? change;
        try
        {
            change = await _vault.CreateAsync(password, replaceUnlocked: false, cancellationToken);
        }
        catch (VaultAlreadyCreatedException)
        {
            // A concurrent confirm wrapped it first; its recovery code is the valid one.
            return new NodeVaultConfirmResult
            {
                Succeeded = false,
                NotPending = true
            };
        }

        _logger.LogInformation("Legacy node.key wrapped into the node vault for user {UserId}.", user.Id);
        return new NodeVaultConfirmResult
        {
            Succeeded = true,
            RecoveryCode = change?.RecoveryCode
        };
    }

    private async Task<NodePasswordChangeResult> ResetIdentityPasswordAsync(NodeUser user, string newPassword, CancellationToken cancellationToken)
    {
        // RemovePassword + AddPassword is the no-old-password reset primitive (Identity has no token-less ResetPassword, and no reset-token
        // provider is registered). Both go in a serializable transaction — as in SetupAsync — so a rejected new password never leaves the account passwordless.
        await using var transaction = await _identity.BeginSerializableTransactionAsync(cancellationToken);

        var removeResult = await _userManager.RemovePasswordAsync(user);
        if (!removeResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = ToErrorList(removeResult)
            };
        }

        var addResult = await _userManager.AddPasswordAsync(user, newPassword);
        if (!addResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new NodePasswordChangeResult
            {
                Succeeded = false,
                Errors = ToErrorList(addResult)
            };
        }

        // A forgotten password is often preceded by failed attempts that tripped the 5-strike lockout; clear it so the
        // operator can sign in immediately with the new password.
        await _userManager.ResetAccessFailedCountAsync(user);
        await _userManager.SetLockoutEndDateAsync(user, lockoutEnd: null);

        await _identity.RevokeActiveTokensAsync(user.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning("Node admin password reset for user {UserId}; refresh tokens revoked and the rotated security "
                           + "stamp invalidates existing access tokens.", user.Id);
        return new NodePasswordChangeResult
        {
            Succeeded = true,
            Errors = []
        };
    }

    /// <summary>
    ///     Whether <paramref name="presented" /> was revoked by ROTATION inside the grace window and its own chain is
    ///     still signed in — rather than revoked by logout, a password change or a reset.
    /// </summary>
    /// <remarks>
    ///     Rotation links the revoked token to its successor; <see cref="RevokeRefreshTokensAsync" />,
    ///     <see cref="ChangePasswordAsync" /> and <see cref="ResetAdminPasswordAsync" /> link nothing and revoke the
    ///     chain's live head, so following the links to the head and requiring it live keeps a logged-out cookie from
    ///     ever being resurrected. Another client's session is never consulted. A real walk is short (every hop is a
    ///     rotation within one window); the hop cap and same-user check make a tampered cycle or cross-user link fail.
    /// </remarks>
    private async Task<bool> IsWithinRotationGraceAsync(NodeRefreshToken presented, DateTime now, CancellationToken cancellationToken)
    {
        if (presented.RevokedAtUtc is not { } revokedAtUtc || revokedAtUtc > now || now - revokedAtUtc > RotationGraceWindow)
        {
            return false;
        }

        var head = presented;
        for (var hops = 0; hops <= MaxRotationChainHops; hops++)
        {
            if (head.ReplacedByTokenId is not { } successorId)
            {
                return head.RevokedAtUtc is null && head.ExpiresAtUtc > now;
            }

            head = await _identity.FindRefreshTokenByIdAsync(successorId, cancellationToken);
            if (head is null || !string.Equals(head.UserId, presented.UserId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    ///     Issues an access token and a refresh token; with <paramref name="rotated" /> the refresh token is that token's
    ///     linked successor, otherwise it replaces nothing.
    /// </summary>
    private async Task<NodeAuthTokenResult> CreateTokenResultAsync(NodeUser user,
        DateTime now,
        NodeRefreshToken? rotated,
        CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var (accessToken, accessTokenExpiresAtUtc) = _tokenService.CreateAccessToken(user, roles);
        var refreshToken = _tokenService.CreateRefreshTokenRaw();
        var refreshTokenExpiresAtUtc = now.AddDays(_options.Value.RefreshTokenDays);
        var stored = new NodeRefreshToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashRefreshToken(refreshToken),
            ExpiresAtUtc = refreshTokenExpiresAtUtc,
            CreatedAtUtc = now
        };

        if (rotated is null)
        {
            await _identity.AddRefreshTokenAsync(stored, cancellationToken);
        }
        else
        {
            await _identity.RotateAsync(rotated, stored, now, cancellationToken);
        }

        return new NodeAuthTokenResult
        {
            Succeeded = true,
            AccessToken = accessToken,
            AccessTokenExpiresAtUtc = accessTokenExpiresAtUtc,
            RefreshToken = refreshToken,
            RefreshTokenExpiresAtUtc = refreshTokenExpiresAtUtc
        };
    }

    private async Task<NodeUser?> ResolveLoginUserAsync(string? email, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            return await _userManager.FindByEmailAsync(email.Trim());
        }

        return await _identity.FindCompletedSetupUserAsync(cancellationToken);
    }

    private Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken)
    {
        return _identity.HasCompletedSetupAsync(cancellationToken);
    }

    /// <summary>
    ///     Whole seconds still left on the user's lockout, floored at one and saturated at <see cref="int.MaxValue" />.
    /// </summary>
    /// <remarks>
    ///     The floor keeps a caller from being told to retry in zero seconds; the saturation keeps an operator-set
    ///     far-future <c>LockoutEnd</c> from overflowing the int. Saturating rather than capping matters: a shorter
    ///     number would tell a caller to retry while the account is still locked, which is the confusion the coded 401
    ///     exists to remove.
    /// </remarks>
    private async Task<int> GetLockoutRetryAfterSecondsAsync(NodeUser user)
    {
        var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
        var now = _timeProvider.GetUtcNow();
        var remainingSeconds = Math.Ceiling(((lockoutEnd ?? now) - now).TotalSeconds);

        return (int)Math.Clamp(remainingSeconds, min: 1, max: int.MaxValue);
    }

    private static NodeAuthTokenResult FailedTokenResult(int? lockedOutRetryAfterSeconds = null)
    {
        return new NodeAuthTokenResult
        {
            Succeeded = false,
            AccessToken = null,
            AccessTokenExpiresAtUtc = null,
            RefreshToken = null,
            RefreshTokenExpiresAtUtc = null,
            LockedOutRetryAfterSeconds = lockedOutRetryAfterSeconds
        };
    }

    private static string GetSignInFailureReason(SignInResult result)
    {
        if (result.IsLockedOut)
        {
            return "LockedOut";
        }

        if (result.IsNotAllowed)
        {
            return "NotAllowed";
        }

        return "InvalidCredentials";
    }

    private static string[] ToErrorList(IdentityResult result)
    {
        return result.Errors.Select(static error => error.Description).ToArray();
    }
}
