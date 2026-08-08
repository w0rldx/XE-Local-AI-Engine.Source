namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;

/// <summary>
///     Query-string request for <c>GET app-update/status</c>. <see cref="Refresh" /> (default false) forces a fresh GitHub
///     check, subject to a 10-minute rate-limit floor (a younger snapshot is served from cache regardless).
/// </summary>
public sealed class GetAppUpdateStatusRequest
{
    /// <summary>When true, re-checks GitHub (rate-limited to once per 10 minutes); null/false serves the cached snapshot.</summary>
    public bool? Refresh { get; init; }
}

/// <summary>
///     Response for <c>GET app-update/status</c>. Surfaces the running version, the available version (when newer),
///     whether an update is available, whether this build is configured and desktop, the sanitized check status, and
///     when it ran.
/// </summary>
public sealed class AppUpdateStatusResponse
{
    /// <summary>The running app version.</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>The newer available version when an update exists; otherwise <see langword="null" />.</summary>
    public string? AvailableVersion { get; init; }

    /// <summary>True only when a newer release was resolved.</summary>
    public required bool UpdateAvailable { get; init; }

    /// <summary>True when the artifact carries a usable public GitHub update source.</summary>
    public required bool IsConfigured { get; init; }

    /// <summary>True when running as the desktop self-update build (React renders the update UI only then).</summary>
    public required bool IsDesktop { get; init; }

    /// <summary>Sanitized result: <c>notChecked</c>, <c>ready</c>, <c>offline</c>, or <c>failed</c>.</summary>
    public required string CheckStatus { get; init; }

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
