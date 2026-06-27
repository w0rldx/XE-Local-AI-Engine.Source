namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     The result of beginning a GitHub device flow. <see cref="DeviceCode" /> is the SECRET polling credential — it
///     stays server-side and is NEVER returned to React or placed in any endpoint DTO; the user sees only
///     <see cref="UserCode" /> and opens <see cref="VerificationUri" />.
/// </summary>
/// <param name="DeviceCode">The secret device verification code used to poll for the token. Server-side only.</param>
/// <param name="UserCode">The short code the user types into <see cref="VerificationUri" /> (e.g. <c>WDJB-MJHT</c>).</param>
/// <param name="VerificationUri">The github.com URL the user opens to enter <see cref="UserCode" />.</param>
/// <param name="ExpiresInSeconds">How long (seconds) the codes remain valid (GitHub default 900).</param>
/// <param name="IntervalSeconds">The minimum seconds the app must wait between polls (respect <c>slow_down</c>).</param>
public sealed record GitHubDeviceFlowStart(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>The state of a device-flow poll exchange.</summary>
public enum GitHubDeviceFlowState
{
    /// <summary>The user has not yet authorized; keep polling at the interval.</summary>
    Pending,

    /// <summary>The user authorized; the token was exchanged and stored. <see cref="GitHubDeviceFlowPoll.Login" /> is set.</summary>
    Authorized,

    /// <summary>The user denied the authorization; stop polling.</summary>
    Denied,

    /// <summary>The device code expired before authorization; the user must start over.</summary>
    Expired
}

/// <summary>
///     The result of one device-flow poll. On <see cref="GitHubDeviceFlowState.Authorized" /> the token has already been
///     persisted by the service and <see cref="Login" /> carries the GitHub login; the token itself is NEVER returned.
/// </summary>
/// <param name="State">The poll outcome.</param>
/// <param name="Login">The authenticated GitHub login on success; <see langword="null" /> otherwise.</param>
public sealed record GitHubDeviceFlowPoll(GitHubDeviceFlowState State, string? Login);

/// <summary>
///     A device-flow / auth transport failure surfaced to the endpoint as a sanitized error. The message is user-safe
///     (no token, no device_code, no internal URL) so it can be returned to React directly.
/// </summary>
public sealed class GitHubAuthException : Exception
{
    public GitHubAuthException(string message) : base(message)
    {
    }

    public GitHubAuthException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
