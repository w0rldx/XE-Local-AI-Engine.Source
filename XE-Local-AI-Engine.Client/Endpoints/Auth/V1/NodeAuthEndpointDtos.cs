namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

/// <summary>
///     Response DTO for node auth status operations.
/// </summary>
public sealed record NodeAuthStatusResponse
{
    public required bool SetupRequired { get; init; }

    public required bool Authenticated { get; init; }
}

/// <summary>
///     Request DTO for node setup operations.
/// </summary>
public sealed record NodeSetupRequest
{
    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}

/// <summary>
///     Request DTO for node login operations.
/// </summary>
public sealed record NodeLoginRequest
{
    public string? Email { get; init; }

    public string Password { get; init; } = string.Empty;
}

/// <summary>
///     Request DTO for node change password operations.
/// </summary>
public sealed record NodeChangePasswordRequest
{
    public string CurrentPassword { get; init; } = string.Empty;

    public string NewPassword { get; init; } = string.Empty;
}

/// <summary>
///     Response DTO for node access token operations.
/// </summary>
public sealed record NodeAccessTokenResponse
{
    public required string AccessToken { get; init; }

    public required DateTime ExpiresAtUtc { get; init; }
}

/// <summary>
///     Response DTO for node me operations.
/// </summary>
public sealed record NodeMeResponse
{
    public required string UserName { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>
///     Response DTO for node auth error operations.
/// </summary>
public sealed record NodeAuthErrorResponse
{
    public required string Message { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}
