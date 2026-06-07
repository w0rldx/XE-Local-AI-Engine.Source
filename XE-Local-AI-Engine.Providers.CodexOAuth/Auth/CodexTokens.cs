namespace XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// A persisted Codex OAuth session. Password-equivalent — never logged, never written to appsettings;
/// persisted only via the encrypted <see cref="CodexTokenStore"/> (plan §9/D4).
/// </summary>
/// <param name="AccessToken">Short-lived bearer token (~1h) sent as <c>Authorization</c>.</param>
/// <param name="RefreshToken">Long-lived refresh token (~30-90d); may rotate (single-use) — refresh is single-flight (M2).</param>
/// <param name="ExpiresUtc">Absolute UTC expiry of <paramref name="AccessToken"/>.</param>
/// <param name="AccountId">ChatGPT account id from the JWT <c>chatgpt_account_id</c> claim, sent as the account-id header.</param>
public sealed record CodexTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresUtc,
    string AccountId)
{
    /// <summary>
    /// True when the access token is at or past expiry once the supplied <paramref name="skew"/> is applied.
    /// </summary>
    public bool IsExpired(TimeSpan skew, DateTimeOffset now)
        => now >= ExpiresUtc - skew;
}
