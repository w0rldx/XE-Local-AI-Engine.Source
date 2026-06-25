namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     GitHub App device-flow sign-in for app self-update. Begins a device flow, exchanges the device code for a user
///     access token (no refresh, no expiry — by design), and signs out by revoking the token server-side at GitHub and
///     clearing the local store. All calls go to github.com over HTTPS using the baked GitHub App client_id. The token is
///     a secret: it is persisted only via <see cref="IGitHubTokenStore" />, never logged, never returned to React.
/// </summary>
public interface IGitHubAuthService
{
    /// <summary>
    ///     Begins a device flow: requests the device + user codes from GitHub. The returned
    ///     <see cref="GitHubDeviceFlowStart.DeviceCode" /> is secret and MUST stay server-side.
    /// </summary>
    /// <exception cref="GitHubAuthException">The device-flow start could not be completed (transport / unconfigured).</exception>
    Task<GitHubDeviceFlowStart> StartAsync(CancellationToken ct);

    /// <summary>
    ///     Polls the token endpoint once with <paramref name="deviceCode" />. On authorization it exchanges for a user
    ///     access token, resolves the GitHub login, and persists the session — then reports
    ///     <see cref="GitHubDeviceFlowState.Authorized" />. Pending / denied / expired are reported without throwing.
    /// </summary>
    /// <exception cref="GitHubAuthException">An unexpected transport / protocol failure (not a normal pending/denied).</exception>
    Task<GitHubDeviceFlowPoll> PollAsync(string deviceCode, CancellationToken ct);

    /// <summary>
    ///     Signs out: revokes the stored token at GitHub (<c>DELETE /applications/{client_id}/token</c>) so a stolen,
    ///     then-signed-out token is dead, then clears the local store. Clearing the local store always happens even when
    ///     the remote revoke fails (best-effort revoke; never throws on a remote failure).
    /// </summary>
    Task SignOutAsync(CancellationToken ct);
}
