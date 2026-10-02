namespace XE_Local_AI_Engine.Client.Endpoints.Auth.V1;

public sealed record NodeAuthStatusResponse
{
    public required bool SetupRequired { get; init; }

    public required bool Authenticated { get; init; }

    /// <summary>
    ///     The node vault state: <c>pending</c>, <c>locked</c> or <c>unlocked</c> (see <see cref="NodeAuthVaultStatus" />).
    ///     An operator-custody secret reports <c>unlocked</c>.
    /// </summary>
    public required string Vault { get; init; }
}

/// <summary>The <c>auth/setup</c> success body.</summary>
public sealed record NodeSetupResponse
{
    /// <summary>The one-time vault recovery code (8 dash-separated base32 groups); <c>null</c> under operator custody.</summary>
    public string? RecoveryCode { get; init; }
}

/// <summary>The <c>auth/vault/confirm</c> body: the signed-in admin re-enters the password to wrap a legacy key.</summary>
public sealed record NodeVaultConfirmRequest
{
    public string Password { get; init; } = string.Empty;
}

public sealed record NodeVaultConfirmResponse
{
    public required string RecoveryCode { get; init; }
}

/// <summary>The locked pre-host's <c>auth/vault/unlock</c> body.</summary>
public sealed record VaultUnlockRequest
{
    public string Password { get; init; } = string.Empty;
}

/// <summary>The locked pre-host's <c>auth/vault/unlock-recovery</c> body: prove the code, set a new password.</summary>
public sealed record VaultRecoveryUnlockRequest
{
    public string RecoveryCode { get; init; } = string.Empty;

    public string NewPassword { get; init; } = string.Empty;
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

/// <summary>
///     The <c>401</c> body <c>auth/login</c> answers when ASP.NET Identity has locked the account, paired with a
///     <c>Retry-After</c> header carrying the same number of seconds.
/// </summary>
/// <remarks>
///     A wrong password before the lockout threshold still answers a body-less <c>401</c>, so <see cref="Code" /> is
///     the only signal that waiting is the fix.
/// </remarks>
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
