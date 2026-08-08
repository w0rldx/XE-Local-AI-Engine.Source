namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Orchestrates app self-update: runs an anonymous public GitHub release check in desktop mode, records the result in
///     <see cref="IAppUpdateState" />, and applies an available update (download → apply → relaunch). Wraps the Velopack
///     manager behind <see cref="IVelopackUpdateManager" /> so it can be tested without real network.
/// </summary>
public interface IAppUpdateService
{
    /// <summary>
    ///     Runs one update check and stores the resulting snapshot. Inert when not desktop or not configured. An offline
    ///     box records an offline status, while malformed or incompatible data records a distinct failed status.
    /// </summary>
    Task<AppUpdateSnapshot> CheckForUpdatesAsync(CancellationToken ct);

    /// <summary>
    ///     Runs a serialized update check only when the latest stored snapshot is older than <paramref name="minInterval" />.
    ///     Concurrent callers share the stored result instead of starting duplicate GitHub requests.
    /// </summary>
    Task<AppUpdateSnapshot> RefreshIfStaleAsync(TimeSpan minInterval, CancellationToken ct);

    /// <summary>
    ///     Downloads the available update and schedules Velopack to apply it after host exit. No-op when not desktop, not
    ///     configured, or no update is available (including when a live re-check finds nothing). The endpoint completes a
    ///     successful response before it stops the host, allowing the browser to enter restart polling reliably.
    /// </summary>
    /// <returns><see langword="true" /> when an apply was actually initiated; <see langword="false" /> when nothing was applied.</returns>
    /// <exception cref="AppUpdateException">The apply failed (sanitized message — no path or feed URL).</exception>
    Task<bool> ApplyAsync(CancellationToken ct);
}

/// <summary>An app-update apply failure surfaced to the endpoint as a sanitized, user-safe error.</summary>
public sealed class AppUpdateException : Exception
{
    public AppUpdateException(string message) : base(message)
    {
    }

    public AppUpdateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
