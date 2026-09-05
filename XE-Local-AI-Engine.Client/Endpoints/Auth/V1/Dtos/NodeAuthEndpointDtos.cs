namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

public sealed record NodeAuthStatusResponse
{
    public required bool SetupRequired { get; init; }

    public required bool Authenticated { get; init; }
}

public sealed record NodeSetupRequest
{
    public string Email { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;
}

public sealed record NodeLoginRequest
{
    public string? Email { get; init; }

    public string Password { get; init; } = string.Empty;
}

public sealed record NodeChangePasswordRequest
{
    public string CurrentPassword { get; init; } = string.Empty;

    public string NewPassword { get; init; } = string.Empty;
}

public sealed record NodeAccessTokenResponse
{
    public required string AccessToken { get; init; }

    public required DateTime ExpiresAtUtc { get; init; }
}

public sealed record NodeMeResponse
{
    public required string UserName { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }
}

/// <summary>
///     The <c>401</c> body <c>auth/login</c> answers when ASP.NET Identity has locked the account, paired with a
///     <c>Retry-After</c> header carrying the same number of seconds. A wrong password before the lockout threshold
///     still answers a body-less <c>401</c>, so <see cref="Code" /> is the only signal that waiting is the fix.
/// </summary>
public sealed record NodeLoginLockedOutResponse
{
    /// <summary>The machine-readable discriminator. Always <c>locked-out</c>.</summary>
    public const string LockedOutCode = "locked-out";

    public required string Message { get; init; }

    public string Code { get; init; } = LockedOutCode;

    public required int RetryAfterSeconds { get; init; }
}

public sealed record NodeAuthErrorResponse
{
    public required string Message { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}
