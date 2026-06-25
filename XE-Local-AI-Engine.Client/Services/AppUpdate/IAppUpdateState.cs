namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>The GitHub sign-in state surfaced to React (never the token itself).</summary>
public enum AppUpdateAuthState
{
    /// <summary>No GitHub session is stored — the user must sign in to check for updates.</summary>
    SignedOut,

    /// <summary>A GitHub session is stored and last accepted by GitHub.</summary>
    SignedIn,

    /// <summary>The stored token was rejected (401 — revoked / lost) — the user must sign in again.</summary>
    ReauthRequired,

    /// <summary>The signed-in user lacks read access to the release repo (403) — they must be added as a collaborator.</summary>
    NoAccess
}

/// <summary>
///     The last-computed app self-update snapshot, shared between the startup/after-sign-in check
///     (<c>AppUpdateCheckService</c> / <see cref="IAppUpdateService" />) and the read-only status endpoint. Holds the
///     running version, the available version (when newer), whether an update is available, the GitHub auth state,
///     whether this build is desktop and configured, whether the last check was offline, and when it ran — so the status
///     endpoint can answer without re-hitting GitHub on every poll. Modeled on <c>LlamaCppUpdateSnapshot</c>.
/// </summary>
/// <param name="CurrentVersion">The running app version.</param>
/// <param name="AvailableVersion">The newer available version when an update exists; otherwise <see langword="null" />.</param>
/// <param name="UpdateAvailable"><see langword="true" /> only when a newer release was resolved.</param>
/// <param name="AuthState">The GitHub sign-in state.</param>
/// <param name="IsDesktop"><see langword="true" /> when running as the desktop self-update build.</param>
/// <param name="IsOffline"><see langword="true" /> when GitHub was unreachable at the time of the snapshot.</param>
/// <param name="Login">The signed-in GitHub login (display only), or <see langword="null" /> when signed out.</param>
/// <param name="LastCheckedUtc">When the snapshot was computed (UTC), or <see langword="null" /> before the first check.</param>
public sealed record AppUpdateSnapshot(
    string CurrentVersion,
    string? AvailableVersion,
    bool UpdateAvailable,
    AppUpdateAuthState AuthState,
    bool IsDesktop,
    bool IsOffline,
    string? Login,
    DateTimeOffset? LastCheckedUtc)
{
    /// <summary>The empty pre-check snapshot: signed out, no update advertised, not offline, version unknown.</summary>
    public static AppUpdateSnapshot Empty { get; } = new(CurrentVersion: "0.0.0",
        AvailableVersion: null,
        UpdateAvailable: false,
        AuthState: AppUpdateAuthState.SignedOut,
        IsDesktop: false,
        IsOffline: false,
        Login: null,
        LastCheckedUtc: null);
}

/// <summary>
///     Thread-safe holder for the latest <see cref="AppUpdateSnapshot" />. Singleton: the check writes it and the status
///     endpoint reads it. A single reference field swapped under <c>Volatile</c> — lock-free reads, atomic writes — so a
///     reader never tears a snapshot. Modeled on <c>ILlamaCppUpdateState</c>.
/// </summary>
public interface IAppUpdateState
{
    /// <summary>The current snapshot (never <see langword="null" />; defaults to <see cref="AppUpdateSnapshot.Empty" />).</summary>
    AppUpdateSnapshot Current { get; }

    /// <summary>Atomically replaces the current snapshot with <paramref name="snapshot" />.</summary>
    void Store(AppUpdateSnapshot snapshot);
}

/// <summary>Default in-memory <see cref="IAppUpdateState" /> — a single reference field swapped under <c>Volatile</c>.</summary>
public sealed class AppUpdateState : IAppUpdateState
{
    private AppUpdateSnapshot _current = AppUpdateSnapshot.Empty;

    public AppUpdateSnapshot Current => Volatile.Read(ref _current);

    public void Store(AppUpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
