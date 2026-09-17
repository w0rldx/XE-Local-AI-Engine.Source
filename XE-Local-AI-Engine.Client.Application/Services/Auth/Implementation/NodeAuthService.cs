namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class NodeAuthService : INodeAuthService
{
    private static readonly SemaphoreSlim SetupLock = new(initialCount: 1, maxCount: 1);

    /// <summary>
    ///     How long a refresh token that ROTATION replaced still buys a successor. The SPA keeps its access token in
    ///     memory only, so every document load refreshes; a reload while a refresh is already in flight, or a second tab,
    ///     makes two requests present the same cookie, and single-use rotation would answer the loser 401 — which clears
    ///     the cookie and signs the operator out although nothing was compromised. Not an option: an operator has no
    ///     reason to tune it, and every second of it is a second a captured cookie stays replayable.
    /// </summary>
    private static readonly TimeSpan RotationGraceWindow = TimeSpan.FromSeconds(10);

    private readonly NodeIdentityDbContext _dbContext;
    private readonly ILogger<NodeAuthService> _logger;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly IOptions<NodeAuthOptions> _options;
    private readonly SignInManager<NodeUser> _signInManager;
    private readonly TimeProvider _timeProvider;
    private readonly INodeTokenService _tokenService;
    private readonly UserManager<NodeUser> _userManager;

    public NodeAuthService(NodeIdentityDbContext dbContext,
        UserManager<NodeUser> userManager,
        SignInManager<NodeUser> signInManager,
        INodeTokenService tokenService,
        IOptions<NodeAuthOptions> options,
        INodeSettingsStore nodeSettingsStore,
        TimeProvider timeProvider,
        ILogger<NodeAuthService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _signInManager = signInManager ?? throw new ArgumentNullException(nameof(signInManager));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeAuthStatus> GetStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var hasAdminUser = await _dbContext.Users
                                           .AsNoTracking()
                                           .AnyAsync(user => user.SetupCompleted, cancellationToken);

        return new NodeAuthStatus(!hasAdminUser, principal.Identity?.IsAuthenticated == true);
    }

    public async Task<NodeSetupResult> SetupAsync(string email, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await SetupLock.WaitAsync(cancellationToken);
        try
        {
            if (await HasCompletedSetupAsync(cancellationToken))
            {
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: true, []);
            }

            await using var transaction = await _dbContext.Database
                                                          .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            if (await HasCompletedSetupAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: true, []);
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
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: false, ToErrorList(createResult));
            }

            var roleResult = await _userManager.AddToRoleAsync(user, NodeAuthorizationPolicies.AdminRole);
            if (!roleResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: false, ToErrorList(roleResult));
            }

            // Written BEFORE the commit, and only when nothing has been chosen yet. The settings file cannot join the
            // identity transaction, so one of the two writes has to be the one that can be orphaned. Writing first makes
            // the orphan "profile pending, no administrator", which is inert: setup fails and is retryable, the gated
            // services keep waiting because there is no operator to ask, and the retry lands on this same null guard.
            // Writing after the commit would instead leave "administrator exists, profile null" reachable — the one
            // state the boot backfill decides as "recommended", which would start outbound checks the operator was
            // never asked about. The token is SetupAsync's own: a cancellation here throws before the commit, so the
            // transaction disposes unconfirmed and Identity rolls back.
            await _nodeSettingsStore.UpdateAsync(latest => latest.ExternalAccessProfile is null
                    ? latest with
                    {
                        ExternalAccessProfile = StoredNodeSettings.ExternalAccessProfilePending
                    }
                    : latest,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("Node admin user created during first-run setup.");
            return new NodeSetupResult(Succeeded: true, AlreadyInitialized: false, []);
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

            // A locked-out login is the ONE failure that is reported distinguishably: an operator who mistyped five
            // times otherwise reads "incorrect password" while holding the right one, and has no way to learn that
            // waiting is the fix. This tells a caller that an email exists once five attempts have been spent, which
            // is accepted on a loopback-only node whose login is additionally capped at 10 requests/minute per IP.
            return signInResult.IsLockedOut
                ? FailedTokenResult(await GetLockoutRetryAfterSecondsAsync(user))
                : FailedTokenResult();
        }

        return await CreateTokenResultAsync(user, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    }

    public async Task<NodeAuthTokenResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return FailedTokenResult();
        }

        var refreshTokenHash = _tokenService.HashRefreshToken(refreshToken);
        await using var transaction = await _dbContext.Database
                                                      .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var storedToken = await _dbContext.RefreshTokens
                                          .SingleOrDefaultAsync(token => token.TokenHash == refreshTokenHash, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (storedToken is null || storedToken.ExpiresAtUtc <= now)
        {
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogWarning("Node refresh failed: missing or expired refresh token.");
            return FailedTokenResult();
        }

        var withinRotationGrace = storedToken.RevokedAtUtc is { } revokedAtUtc
                                  && await WasReplacedByRotationAsync(storedToken.UserId, revokedAtUtc, now, cancellationToken);

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
            // Deliberately NOT re-stamped: the window is measured from the ORIGINAL rotation, so presenting the same
            // token again every few seconds cannot walk it forward into an unbounded replay window.
            _logger.LogInformation("Node refresh honoured a token that rotation replaced inside the grace window for user {UserId}.",
                storedToken.UserId);
        }
        else
        {
            storedToken.RevokedAtUtc = now;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var result = await CreateTokenResultAsync(user, now, cancellationToken);
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

        await RevokeActiveTokensAsync(user.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
    }

    public async Task<NodePasswordChangeResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
        {
            return new NodePasswordChangeResult(Succeeded: false, ["The current session is invalid."]);
        }

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
        {
            return new NodePasswordChangeResult(Succeeded: false, ToErrorList(result));
        }

        await RevokeActiveTokensAsync(user.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        return new NodePasswordChangeResult(Succeeded: true, []);
    }

    public async Task<NodePasswordChangeResult> ResetAdminPasswordAsync(string newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        // No email on a recovery reset: resolve the single completed-setup admin, exactly as login does when the UI omits
        // the email (single-user model). Nothing to reset if first-run setup never happened.
        var user = await ResolveLoginUserAsync(email: null, cancellationToken);
        if (user is null)
        {
            return new NodePasswordChangeResult(Succeeded: false,
                ["No administrator account exists. Complete first-run setup before resetting the password."]);
        }

        // RemovePassword + AddPassword is the no-old-password reset primitive (Identity has no token-less ResetPassword,
        // and no reset-token provider is registered). Wrap both in a serializable transaction — mirroring SetupAsync — so a
        // policy-rejected new password rolls back and never leaves the account in the passwordless intermediate state.
        await using var transaction = await _dbContext.Database
                                                      .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var removeResult = await _userManager.RemovePasswordAsync(user);
        if (!removeResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new NodePasswordChangeResult(Succeeded: false, ToErrorList(removeResult));
        }

        var addResult = await _userManager.AddPasswordAsync(user, newPassword);
        if (!addResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new NodePasswordChangeResult(Succeeded: false, ToErrorList(addResult));
        }

        // A forgotten password is often preceded by failed attempts that tripped the 5-strike lockout; clear it so the
        // operator can sign in immediately with the new password.
        await _userManager.ResetAccessFailedCountAsync(user);
        await _userManager.SetLockoutEndDateAsync(user, lockoutEnd: null);

        await RevokeActiveTokensAsync(user.Id, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogWarning("Node admin password reset for user {UserId}; refresh tokens revoked and the rotated security "
                           + "stamp invalidates existing access tokens.", user.Id);
        return new NodePasswordChangeResult(Succeeded: true, []);
    }

    public async Task<NodeCurrentUser?> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(principal);
        if (user is null)
        {
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user);
        return new NodeCurrentUser(user.UserName ?? user.Email ?? user.Id, roles.ToArray());
    }

    /// <summary>
    ///     Whether the revocation at <paramref name="revokedAtUtc" /> was ROTATION replacing the presented token, rather
    ///     than logout, a password change or a reset revoking it. Nothing records WHY a token was revoked, so the
    ///     discriminator is the successor: rotation stamps the revocation and the replacement from one instant (both
    ///     take the caller's <c>now</c>), so a still-live token created at exactly that instant is rotation's own
    ///     successor. <see cref="RevokeRefreshTokensAsync" />, <see cref="ChangePasswordAsync" /> and
    ///     <see cref="ResetAdminPasswordAsync" /> revoke without issuing anything, so they leave no such token and a
    ///     logged-out cookie can never be resurrected here.
    /// </summary>
    private Task<bool> WasReplacedByRotationAsync(string userId, DateTime revokedAtUtc, DateTime now, CancellationToken cancellationToken)
    {
        if (revokedAtUtc > now || now - revokedAtUtc > RotationGraceWindow)
        {
            return Task.FromResult(false);
        }

        return _dbContext.RefreshTokens
                         .AnyAsync(token => token.UserId == userId
                                            && token.RevokedAtUtc == null
                                            && token.ExpiresAtUtc > now
                                            && token.CreatedAtUtc == revokedAtUtc,
                             cancellationToken);
    }

    private async Task<NodeAuthTokenResult> CreateTokenResultAsync(NodeUser user, DateTime now, CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user);
        var (accessToken, accessTokenExpiresAtUtc) = _tokenService.CreateAccessToken(user, roles);
        var refreshToken = _tokenService.CreateRefreshTokenRaw();
        var refreshTokenExpiresAtUtc = now.AddDays(_options.Value.RefreshTokenDays);

        await RevokeActiveTokensAsync(user.Id, now, cancellationToken);
        _dbContext.RefreshTokens.Add(new NodeRefreshToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashRefreshToken(refreshToken),
            ExpiresAtUtc = refreshTokenExpiresAtUtc,
            CreatedAtUtc = now
        });
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new NodeAuthTokenResult(Succeeded: true, accessToken, accessTokenExpiresAtUtc, refreshToken, refreshTokenExpiresAtUtc);
    }

    /// <summary>
    ///     Revokes every live refresh token of <paramref name="userId" />, stamping <paramref name="now" />. The caller
    ///     supplies the instant so that rotation's revoke-and-reissue share one — the clock read that
    ///     <see cref="WasReplacedByRotationAsync" /> reads back as "this token was replaced, not logged out".
    /// </summary>
    private async Task RevokeActiveTokensAsync(string userId, DateTime now, CancellationToken cancellationToken)
    {
        var activeTokens = await _dbContext.RefreshTokens
                                           .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
                                           .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
        }

        if (activeTokens.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<NodeUser?> ResolveLoginUserAsync(string? email, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            return await _userManager.FindByEmailAsync(email.Trim());
        }

        return await _dbContext.Users
                               .SingleOrDefaultAsync(user => user.SetupCompleted, cancellationToken);
    }

    private Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Users.AnyAsync(user => user.SetupCompleted, cancellationToken);
    }

    /// <summary>
    ///     Whole seconds still left on the user's lockout, floored at one so a caller is never told to retry in zero
    ///     seconds and saturated at <see cref="int.MaxValue" /> so an operator-set far-future <c>LockoutEnd</c> cannot
    ///     overflow the int. Saturating rather than capping matters: a shorter number would tell a caller to retry
    ///     while the account is still locked, which is the confusion the coded 401 exists to remove.
    /// </summary>
    private async Task<int> GetLockoutRetryAfterSecondsAsync(NodeUser user)
    {
        var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user);
        var now = _timeProvider.GetUtcNow();
        var remainingSeconds = Math.Ceiling(((lockoutEnd ?? now) - now).TotalSeconds);

        return (int)Math.Clamp(remainingSeconds, min: 1, max: int.MaxValue);
    }

    private static NodeAuthTokenResult FailedTokenResult(int? lockedOutRetryAfterSeconds = null)
    {
        return new NodeAuthTokenResult(Succeeded: false,
            AccessToken: null,
            AccessTokenExpiresAtUtc: null,
            RefreshToken: null,
            RefreshTokenExpiresAtUtc: null,
            LockedOutRetryAfterSeconds: lockedOutRetryAfterSeconds);
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
