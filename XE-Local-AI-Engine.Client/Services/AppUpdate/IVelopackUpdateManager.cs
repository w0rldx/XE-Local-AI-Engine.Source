namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>The outcome of a Velopack update check, with all Velopack types kept behind the seam.</summary>
public enum VelopackCheckOutcome
{
    /// <summary>A check completed and a newer release is available (<see cref="VelopackCheckResult.AvailableVersion" /> set).</summary>
    UpdateAvailable,

    /// <summary>A check completed and no newer release is available.</summary>
    UpToDate,

    /// <summary>The check could not reach the feed because of a documented transport or timeout failure.</summary>
    Offline,

    /// <summary>The check reached a non-transient failure and must not be presented as an offline condition.</summary>
    Failed
}

/// <summary>Sanitized diagnostic category for a failed update check. No exception message, URL, or path is retained.</summary>
public enum AppUpdateFailureReason
{
    None,
    Transport,
    Timeout,
    Tls,
    MalformedFeed,
    Integrity,
    Http,
    Unexpected
}

/// <summary>The result of <see cref="IVelopackUpdateManager.CheckForUpdateAsync" />.</summary>
/// <param name="Outcome">The check outcome.</param>
/// <param name="AvailableVersion">The newer version when <see cref="Outcome" /> is <see cref="VelopackCheckOutcome.UpdateAvailable" />; otherwise <see langword="null" />.</param>
public sealed record VelopackCheckResult(VelopackCheckOutcome Outcome,
    string? AvailableVersion,
    AppUpdateFailureReason FailureReason = AppUpdateFailureReason.None);

/// <summary>
///     The seam over Velopack's <c>UpdateManager</c> for a single build flavor's GitHub source. It keeps every Velopack
///     type (UpdateManager, GithubSource, UpdateInfo, VelopackAsset) inside the implementation so the update service and
///     its tests depend only on this interface — no real network, no Velopack types, in unit tests. One instance is
///     created for the baked anonymous public source policy.
/// </summary>
public interface IVelopackUpdateManager
{
    /// <summary><see langword="true" /> only when the app is running from a Velopack install (not a raw-exe / dev run).</summary>
    bool IsInstalled { get; }

    /// <summary>The currently installed app version, or <c>"0.0.0"</c> when not a Velopack install.</summary>
    string CurrentVersion { get; }

    /// <summary>Checks GitHub for a newer release and returns a sanitized outcome and diagnostic category.</summary>
    Task<VelopackCheckResult> CheckForUpdateAsync(CancellationToken ct);

    /// <summary>
    ///     Downloads the latest release and asks Velopack to wait for this host to exit before applying it and restarting
    ///     into the new version. Re-uses <paramref name="restartArgs" /> so desktop mode + the persisted loopback port
    ///     survive. This method deliberately does not terminate the process: the endpoint must first complete its
    ///     <c>{ applying: true }</c> response, then stop the host gracefully.
    /// </summary>
    /// <returns><see langword="true" /> after the updater is waiting for host exit; <see langword="false" /> when no update was available.</returns>
    Task<bool> PrepareUpdateAndRestartAsync(IReadOnlyList<string> restartArgs, CancellationToken ct);
}

/// <summary>
///     Builds an <see cref="IVelopackUpdateManager" /> bound to the running build flavor's anonymous public GitHub source.
/// </summary>
public interface IVelopackUpdateManagerFactory
{
    /// <summary>Creates a manager against the baked public source policy.</summary>
    IVelopackUpdateManager Create();
}
