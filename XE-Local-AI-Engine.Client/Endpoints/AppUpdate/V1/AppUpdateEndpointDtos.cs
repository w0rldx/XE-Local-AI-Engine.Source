namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

// ---------------------------------------------------------------------------
// GitHub device-flow DTOs
//
// SECURITY INVARIANT: none of these contracts carries the GitHub access token OR the device_code. The device_code stays
// server-side (sec H4); start returns only the user code + verification URI, and the token is held only in the encrypted
// store. Tests AppUpdateContracts_ContainNoTokenField + GitHubAuth_Start_DoesNotReturnDeviceCode assert this.
// ---------------------------------------------------------------------------

/// <summary>
///     Response for <c>POST github-auth/start</c>: the user-facing device-flow prompt. The secret <c>device_code</c> is
///     deliberately ABSENT — it is held server-side and replayed by <c>poll</c> (the React client polls
///     <c>github-auth/poll</c>, which reads the in-flight device code from the server, not from this DTO).
/// </summary>
public sealed class GitHubAuthStartResponse
{
    /// <summary>The short code the user types at <see cref="VerificationUri" /> (e.g. <c>WDJB-MJHT</c>).</summary>
    public required string UserCode { get; init; }

    /// <summary>The github.com URL the user opens to enter the <see cref="UserCode" /> (validated host == github.com).</summary>
    public required string VerificationUri { get; init; }

    /// <summary>Seconds until the codes expire (the user must finish within this window).</summary>
    public required int ExpiresInSeconds { get; init; }

    /// <summary>Minimum seconds the client must wait between <c>poll</c> calls.</summary>
    public required int IntervalSeconds { get; init; }
}

/// <summary>
///     Response for <c>POST github-auth/poll</c>: the device-flow poll outcome. On <c>authorized</c> the token has already
///     been stored server-side and <see cref="Login" /> carries the GitHub login — the token itself is NEVER returned.
/// </summary>
public sealed class GitHubAuthPollResponse
{
    /// <summary>Poll state — <c>pending</c>, <c>authorized</c>, <c>denied</c>, or <c>expired</c> (lowercase).</summary>
    public required string State { get; init; }

    /// <summary>The signed-in GitHub login on <c>authorized</c>; otherwise <see langword="null" />.</summary>
    public string? Login { get; init; }
}

/// <summary>
///     Response for <c>GET github-auth/status</c>: the current GitHub sign-in state. Reports presence + login only — never
///     the token.
/// </summary>
public sealed class GitHubAuthStatusResponse
{
    /// <summary>Auth state — <c>signedOut</c>, <c>signedIn</c>, <c>reauthRequired</c>, or <c>noAccess</c> (lowercase).</summary>
    public required string AuthState { get; init; }

    /// <summary>The signed-in GitHub login when signed in; otherwise <see langword="null" />.</summary>
    public string? Login { get; init; }
}

// ---------------------------------------------------------------------------
// App-update status / apply DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Query-string request for <c>GET app-update/status</c>. <see cref="Refresh" /> (default false) forces a fresh GitHub
///     check, subject to a 60s rate-limit floor (a younger snapshot is served from cache regardless).
/// </summary>
public sealed class GetAppUpdateStatusRequest
{
    /// <summary>When true, re-checks GitHub (rate-limited to once per 60s); null/false serves the cached snapshot.</summary>
    public bool? Refresh { get; init; }
}

/// <summary>
///     Response for <c>GET app-update/status</c>. Surfaces the running version, the available version (when newer),
///     whether an update is available, the GitHub auth state, whether this build is the desktop self-update build,
///     whether the last check was offline, and when it ran. It NEVER carries the GitHub token.
/// </summary>
public sealed class AppUpdateStatusResponse
{
    /// <summary>The running app version.</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>The newer available version when an update exists; otherwise <see langword="null" />.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>True only when a newer release was resolved.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>Auth state — <c>signedOut</c>, <c>signedIn</c>, <c>reauthRequired</c>, or <c>noAccess</c> (lowercase).</summary>
    public required string AuthState { get; init; }

    /// <summary>The signed-in GitHub login when signed in; otherwise <see langword="null" />.</summary>
    public string? Login { get; init; }

    /// <summary>True when running as the desktop self-update build (React renders the update UI only then).</summary>
    public required bool IsDesktop { get; init; }

    /// <summary>True when GitHub was unreachable at the time of the snapshot.</summary>
    public required bool IsOffline { get; init; }

    /// <summary>Unix-ms instant the last check ran (UTC); null before the first check.</summary>
    public long? LastCheckedUtc { get; init; }
}

/// <summary>
///     Response for <c>POST app-update/apply</c>. On success the process relaunches into the new version, so this is
///     returned only when there was nothing to apply (or before the relaunch takes effect). <see cref="Applying" />
///     reflects the REAL apply outcome — the service live-re-checks GitHub, so a stale "update available" snapshot that
///     has since gone away yields <c>false</c> rather than stranding the client waiting for a relaunch.
/// </summary>
public sealed class ApplyAppUpdateResponse
{
    /// <summary>True when an update was actually found and apply was initiated (the app will relaunch); false when none was available.</summary>
    public required bool Applying { get; init; }
}
