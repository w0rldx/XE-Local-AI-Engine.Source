namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>The outcome of a Velopack update check, with all Velopack types kept behind the seam.</summary>
public enum VelopackCheckOutcome
{
    /// <summary>A check completed and a newer release is available (<see cref="VelopackCheckResult.AvailableVersion" /> set).</summary>
    UpdateAvailable,

    /// <summary>A check completed and no newer release is available.</summary>
    UpToDate,

    /// <summary>GitHub rejected the token (401) — the user must re-authenticate.</summary>
    Unauthorized,

    /// <summary>The signed-in user lacks read access to the release repo (403).</summary>
    Forbidden,

    /// <summary>GitHub was unreachable / the feed could not be read — treat as offline, advertise no update.</summary>
    Offline
}

/// <summary>The result of <see cref="IVelopackUpdateManager.CheckForUpdateAsync" />.</summary>
/// <param name="Outcome">The check outcome.</param>
/// <param name="AvailableVersion">The newer version when <see cref="Outcome" /> is <see cref="VelopackCheckOutcome.UpdateAvailable" />; otherwise <see langword="null" />.</param>
public sealed record VelopackCheckResult(VelopackCheckOutcome Outcome, string? AvailableVersion);

/// <summary>
///     The seam over Velopack's <c>UpdateManager</c> for a single build flavor's GitHub source. It keeps every Velopack
///     type (UpdateManager, GithubSource, UpdateInfo, VelopackAsset) inside the implementation so the update service and
///     its tests depend only on this interface — no real network, no Velopack types, in unit tests. One instance is
///     created per access token (the token is supplied at construction by the factory).
/// </summary>
public interface IVelopackUpdateManager
{
    /// <summary><see langword="true" /> only when the app is running from a Velopack install (not a raw-exe / dev run).</summary>
    bool IsInstalled { get; }

    /// <summary>The currently installed app version, or <c>"0.0.0"</c> when not a Velopack install.</summary>
    string CurrentVersion { get; }

    /// <summary>Checks GitHub for a newer release, mapping auth / offline failures to a <see cref="VelopackCheckResult" /> (never throws on those).</summary>
    Task<VelopackCheckResult> CheckForUpdateAsync(CancellationToken ct);

    /// <summary>
    ///     Downloads the latest release and applies it, then restarts into the new version (re-using
    ///     <paramref name="restartArgs" /> so desktop mode + the persisted loopback port survive). No-op when no update is
    ///     available. When an update IS applied the process is replaced and this never returns; it returns
    ///     <see langword="false" /> only when the live re-check found nothing to apply.
    /// </summary>
    /// <returns><see langword="false" /> when no update was available to apply (success replaces the process, so it never returns <see langword="true" />).</returns>
    Task<bool> ApplyUpdateAndRestartAsync(IReadOnlyList<string> restartArgs, CancellationToken ct);
}

/// <summary>
///     Builds an <see cref="IVelopackUpdateManager" /> bound to the running build flavor's GitHub repo + a user access
///     token. Behind a factory so the update service can construct a manager per check with the current token, and so
///     tests substitute the whole construction.
/// </summary>
public interface IVelopackUpdateManagerFactory
{
    /// <summary>Creates a manager for <paramref name="accessToken" /> against the baked flavor repo (prerelease included).</summary>
    IVelopackUpdateManager Create(string accessToken);
}
