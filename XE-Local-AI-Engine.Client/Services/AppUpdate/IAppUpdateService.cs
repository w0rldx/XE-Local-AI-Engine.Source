namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Orchestrates app self-update: runs a GitHub-backed update check (desktop + signed-in only), records the result in
///     <see cref="IAppUpdateState" />, and applies an available update (download → apply → relaunch). Wraps the Velopack
///     manager behind <see cref="IVelopackUpdateManager" /> so it can be tested without real network. The GitHub token is
///     read from <see cref="IGitHubTokenStore" /> and never leaves this layer.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>
    ///     Runs one update check and stores the resulting snapshot. Inert (records a signed-out/empty snapshot) when not
    ///     desktop, not configured, or signed out — no GitHub call is made in those cases. A 401/403 records
    ///     <c>reauthRequired</c>/<c>noAccess</c>; an offline box records an <c>isOffline</c> snapshot. Never throws on a
    ///     network/auth failure.
    /// </summary>
    Task<AppUpdateSnapshot> CheckForUpdatesAsync(CancellationToken ct);

    /// <summary>
    ///     Downloads and applies the available update, then relaunches into the new version. No-op when not desktop, not
    ///     signed in, or no update is available (including when a live re-check finds nothing). On a real apply the process
    ///     is replaced and this does not return; otherwise it returns <see langword="false" />.
    /// </summary>
    /// <returns><see langword="true" /> when an apply was actually initiated; <see langword="false" /> when nothing was applied (inert host, signed out, or the live re-check found no update).</returns>
    /// <exception cref="AppUpdateException">The apply failed (sanitized message — no token / path).</exception>
    Task<bool> ApplyAsync(CancellationToken ct);
}

/// <summary>An app-update apply failure surfaced to the endpoint as a sanitized, user-safe error (no token / path / URL).</summary>
public sealed class AppUpdateException : Exception
{
    public AppUpdateException(string message) : base(message)
    {
    }

    public AppUpdateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
