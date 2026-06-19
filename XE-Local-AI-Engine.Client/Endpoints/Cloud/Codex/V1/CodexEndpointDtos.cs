namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

/// <summary>
///     Response to <c>POST cloud/codex/login</c>: the PKCE authorize URL the operator opens in a browser. The
///     loopback callback completes the sign-in in the background; the UI polls <see cref="CodexStatusResponse" />.
///     Carries no token material.
/// </summary>
public sealed record CodexLoginResponse
{
    /// <summary>The authorize URL to open / copy. Contains no secrets.</summary>
    public required string AuthorizeUrl { get; init; }
}

/// <summary>
///     Response to <c>GET cloud/codex/status</c>: the current Codex session / login state. Carries no token
///     material — only whether a session exists, the (non-secret) account id, the access-token expiry, and whether
///     a browser login is currently pending.
/// </summary>
public sealed record CodexStatusResponse
{
    /// <summary>Whether a Codex OAuth session is currently stored.</summary>
    public required bool SignedIn { get; init; }

    /// <summary>The ChatGPT account id from the session, when signed in.</summary>
    public string? AccountId { get; init; }

    /// <summary>The UTC expiry of the stored access token, when signed in.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>Whether a loopback browser login is currently in flight (awaiting the callback).</summary>
    public required bool LoginPending { get; init; }
}
