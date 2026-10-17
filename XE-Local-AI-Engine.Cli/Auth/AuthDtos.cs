namespace XE_Local_AI_Engine.Cli.Auth;

/// <summary><c>GET auth/status</c>; <see cref="Vault" /> is <c>pending</c>, <c>locked</c> or <c>unlocked</c>.</summary>
internal sealed class NodeAuthStatus
{
    public bool SetupRequired { get; init; }

    public bool Authenticated { get; init; }

    public string Vault { get; init; } = string.Empty;
}

/// <summary><c>POST auth/login</c> in body mode: the refresh token comes back in the body and no cookie is set.</summary>
internal sealed class LoginRequest
{
    public string? Email { get; init; }

    public string Password { get; init; } = string.Empty;

    public bool ReturnRefreshToken { get; init; }
}

/// <summary>The body-mode answer of <c>auth/login</c> and <c>auth/refresh</c>; also the token file's content.</summary>
internal sealed class TokenPair
{
    public string AccessToken { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; }

    public string? RefreshToken { get; init; }

    public DateTimeOffset? RefreshTokenExpiresAtUtc { get; init; }

    /// <summary>The node origin the pair was issued by; set by the CLI, never by the node.</summary>
    public string? Origin { get; init; }
}

/// <summary><c>POST auth/vault/unlock</c> on the locked pre-host.</summary>
internal sealed class VaultUnlockRequest
{
    public string Password { get; init; } = string.Empty;
}
