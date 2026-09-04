namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeAuthService : INodeAuthService
{
    private static readonly SemaphoreSlim SetupLock = new(initialCount: 1, maxCount: 1);

    private readonly NodeIdentityDbContext _dbContext;
    private readonly ILogger<NodeAuthService> _logger;
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
        TimeProvider timeProvider,
        ILogger<NodeAuthService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _signInManager = signInManager ?? throw new ArgumentNullException(nameof(signInManager));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<NodeAuthStatus> GetStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var hasAdminUser = await _dbContext.Users
                                           .AsNoTracking()
                                           .AnyAsync(user => user.SetupCompleted, cancellationToken)
                                           .ConfigureAwait(false);

        return new NodeAuthStatus(!hasAdminUser, principal.Identity?.IsAuthenticated == true);
    }

    public async Task<NodeSetupResult> SetupAsync(string email, string password, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await SetupLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await HasCompletedSetupAsync(cancellationToken).ConfigureAwait(false))
            {
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: true, []);
            }

            await using var transaction = await _dbContext.Database
                                                          .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                                                          .ConfigureAwait(false);

            if (await HasCompletedSetupAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
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

            var createResult = await _userManager.CreateAsync(user, password).ConfigureAwait(false);
            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: false, ToErrorList(createResult));
            }

            var roleResult = await _userManager.AddToRoleAsync(user, NodeAuthorizationPolicies.AdminRole).ConfigureAwait(false);
            if (!roleResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new NodeSetupResult(Succeeded: false, AlreadyInitialized: false, ToErrorList(roleResult));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

        var user = await ResolveLoginUserAsync(email, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            _logger.LogWarning("Node login failed: no matching user.");
            return FailedTokenResult();
        }

        var signInResult = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true).ConfigureAwait(false);
        if (!signInResult.Succeeded)
        {
            _logger.LogWarning("Node login failed for user {UserId}: {Reason}.", user.Id, GetSignInFailureReason(signInResult));

            // A locked-out login is the ONE failure that is reported distinguishably: an operator who mistyped five
            // times otherwise reads "incorrect password" while holding the right one, and has no way to learn that
            // waiting is the fix. This tells a caller that an email exists once five attempts have been spent, which
            // is accepted on a loopback-only node whose login is additionally capped at 10 requests/minute per IP.
            return signInResult.IsLockedOut
                ? FailedTokenResult(await GetLockoutRetryAfterSecondsAsync(user).ConfigureAwait(false))
                : FailedTokenResult();
        }

        return await CreateTokenResultAsync(user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeAuthTokenResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return FailedTokenResult();
        }

        var refreshTokenHash = _tokenService.HashRefreshToken(refreshToken);
        await using var transaction = await _dbContext.Database
                                                      .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                                                      .ConfigureAwait(false);

        var storedToken = await _dbContext.RefreshTokens
                                          .SingleOrDefaultAsync(token => token.TokenHash == refreshTokenHash, cancellationToken)
                                          .ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        if (storedToken is null || storedToken.RevokedAtUtc is not null || storedToken.ExpiresAtUtc <= now)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Node refresh failed: missing, revoked, or expired refresh token.");
            return FailedTokenResult();
        }

        var user = await _userManager.FindByIdAsync(storedToken.UserId).ConfigureAwait(false);
        if (user is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Node refresh failed: user {UserId} not found.", storedToken.UserId);
            return FailedTokenResult();
        }

        storedToken.RevokedAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var result = await CreateTokenResultAsync(user, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task RevokeRefreshTokensAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(principal).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        await RevokeActiveTokensAsync(user.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodePasswordChangeResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        var user = await _userManager.GetUserAsync(principal).ConfigureAwait(false);
        if (user is null)
        {
            return new NodePasswordChangeResult(Succeeded: false, ["The current session is invalid."]);
        }

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new NodePasswordChangeResult(Succeeded: false, ToErrorList(result));
        }

        await RevokeActiveTokensAsync(user.Id, cancellationToken).ConfigureAwait(false);
        return new NodePasswordChangeResult(Succeeded: true, []);
    }

    public async Task<NodePasswordChangeResult> ResetAdminPasswordAsync(string newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newPassword);

        // No email on a recovery reset: resolve the single completed-setup admin, exactly as login does when the UI omits
        // the email (single-user model). Nothing to reset if first-run setup never happened.
        var user = await ResolveLoginUserAsync(email: null, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return new NodePasswordChangeResult(Succeeded: false,
                ["No administrator account exists. Complete first-run setup before resetting the password."]);
        }

        // RemovePassword + AddPassword is the no-old-password reset primitive (Identity has no token-less ResetPassword,
        // and no reset-token provider is registered). Wrap both in a serializable transaction — mirroring SetupAsync — so a
        // policy-rejected new password rolls back and never leaves the account in the passwordless intermediate state.
        await using var transaction = await _dbContext.Database
                                                      .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                                                      .ConfigureAwait(false);

        var removeResult = await _userManager.RemovePasswordAsync(user).ConfigureAwait(false);
        if (!removeResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new NodePasswordChangeResult(Succeeded: false, ToErrorList(removeResult));
        }

        var addResult = await _userManager.AddPasswordAsync(user, newPassword).ConfigureAwait(false);
        if (!addResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new NodePasswordChangeResult(Succeeded: false, ToErrorList(addResult));
        }

        // A forgotten password is often preceded by failed attempts that tripped the 5-strike lockout; clear it so the
        // operator can sign in immediately with the new password.
        await _userManager.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        await _userManager.SetLockoutEndDateAsync(user, lockoutEnd: null).ConfigureAwait(false);

        await RevokeActiveTokensAsync(user.Id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Node admin password reset for user {UserId}; refresh tokens revoked and the rotated security "
                           + "stamp invalidates existing access tokens.", user.Id);
        return new NodePasswordChangeResult(Succeeded: true, []);
    }

    public async Task<NodeCurrentUser?> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(principal).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var roles = await _userManager.GetRolesAsync(user).ConfigureAwait(false);
        return new NodeCurrentUser(user.UserName ?? user.Email ?? user.Id, roles.ToArray());
    }

    private async Task<NodeAuthTokenResult> CreateTokenResultAsync(NodeUser user, CancellationToken cancellationToken)
    {
        var roles = await _userManager.GetRolesAsync(user).ConfigureAwait(false);
        var (accessToken, accessTokenExpiresAtUtc) = _tokenService.CreateAccessToken(user, roles);
        var refreshToken = _tokenService.CreateRefreshTokenRaw();
        var refreshTokenExpiresAtUtc = _timeProvider.GetUtcNow().AddDays(_options.Value.RefreshTokenDays).UtcDateTime;
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await RevokeActiveTokensAsync(user.Id, cancellationToken).ConfigureAwait(false);
        _dbContext.RefreshTokens.Add(new NodeRefreshToken
        {
            UserId = user.Id,
            TokenHash = _tokenService.HashRefreshToken(refreshToken),
            ExpiresAtUtc = refreshTokenExpiresAtUtc,
            CreatedAtUtc = now
        });
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new NodeAuthTokenResult(Succeeded: true, accessToken, accessTokenExpiresAtUtc, refreshToken, refreshTokenExpiresAtUtc);
    }

    private async Task RevokeActiveTokensAsync(string userId, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var activeTokens = await _dbContext.RefreshTokens
                                           .Where(token => token.UserId == userId && token.RevokedAtUtc == null)
                                           .ToListAsync(cancellationToken)
                                           .ConfigureAwait(false);

        foreach (var token in activeTokens)
        {
            token.RevokedAtUtc = now;
        }

        if (activeTokens.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NodeUser?> ResolveLoginUserAsync(string? email, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            return await _userManager.FindByEmailAsync(email.Trim()).ConfigureAwait(false);
        }

        return await _dbContext.Users
                               .SingleOrDefaultAsync(user => user.SetupCompleted, cancellationToken)
                               .ConfigureAwait(false);
    }

    private Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken)
    {
        return _dbContext.Users.AnyAsync(user => user.SetupCompleted, cancellationToken);
    }

    /// <summary>
    ///     Whole seconds still left on the user's lockout, floored at one so a caller is never told to retry in zero
    ///     seconds, and capped at a day so an operator-set far-future <c>LockoutEnd</c> cannot overflow the int.
    /// </summary>
    private async Task<int> GetLockoutRetryAfterSecondsAsync(NodeUser user)
    {
        var lockoutEnd = await _userManager.GetLockoutEndDateAsync(user).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        var remainingSeconds = Math.Ceiling(((lockoutEnd ?? now) - now).TotalSeconds);

        return (int)Math.Clamp(remainingSeconds, min: 1, max: TimeSpan.FromDays(1).TotalSeconds);
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
