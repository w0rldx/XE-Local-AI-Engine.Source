namespace XE_Local_AI_Engine.Client.Services.Auth;

using System.Security.Claims;

/// <summary>
///     Application service for i node auth behavior.
/// </summary>
public interface INodeAuthService
{
    Task<NodeAuthStatus> GetStatusAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<NodeSetupResult> SetupAsync(string email, string password, CancellationToken cancellationToken);

    Task<NodeAuthTokenResult> LoginAsync(string? email, string password, CancellationToken cancellationToken);

    Task<NodeAuthTokenResult> RefreshAsync(string? refreshToken, CancellationToken cancellationToken);

    Task RevokeRefreshTokensAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    Task<NodePasswordChangeResult> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword, CancellationToken cancellationToken);

    Task<NodeCurrentUser?> GetCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

/// <summary>
///     Value object carrying node auth status data.
/// </summary>
public sealed record NodeAuthStatus(bool SetupRequired, bool Authenticated);

/// <summary>
///     Value object carrying node auth token result data.
/// </summary>
public sealed record NodeAuthTokenResult(bool Succeeded, string? AccessToken, DateTime? AccessTokenExpiresAtUtc, string? RefreshToken, DateTime? RefreshTokenExpiresAtUtc);

/// <summary>
///     Value object carrying node setup result data.
/// </summary>
public sealed record NodeSetupResult(bool Succeeded, bool AlreadyInitialized, IReadOnlyList<string> Errors);

/// <summary>
///     Value object carrying node password change result data.
/// </summary>
public sealed record NodePasswordChangeResult(bool Succeeded, IReadOnlyList<string> Errors);

/// <summary>
///     Value object carrying node current user data.
/// </summary>
public sealed record NodeCurrentUser(string UserName, IReadOnlyList<string> Roles);
