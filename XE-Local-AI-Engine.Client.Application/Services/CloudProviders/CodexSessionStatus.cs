namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     The Codex OAuth session as the Operator endpoints see it: presence, the non-secret account id, the
///     access-token expiry and whether a browser login is in flight. Carries no token material.
/// </summary>
public sealed class CodexSessionStatus
{
    /// <summary>True only while a stored session's access token is still valid (skew-adjusted).</summary>
    public required bool SignedIn { get; init; }

    /// <summary>Non-secret ChatGPT account id of the stored session, or <see langword="null" />.</summary>
    public required string? AccountId { get; init; }

    /// <summary>Absolute UTC expiry of the stored session's access token, or <see langword="null" />.</summary>
    public required DateTimeOffset? ExpiresAtUtc { get; init; }

    /// <summary>True while a loopback PKCE login is still exchanging in the background.</summary>
    public required bool LoginPending { get; init; }
}
