namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>Sanitized state of the most recent app-update check.</summary>
public enum AppUpdateCheckStatus
{
    NotChecked,
    Ready,
    Offline,
    Failed
}

/// <summary>The last-computed public app-update snapshot.</summary>
public sealed record AppUpdateSnapshot(
    string CurrentVersion,
    string? AvailableVersion,
    bool UpdateAvailable,
    bool IsConfigured,
    bool IsDesktop,
    AppUpdateCheckStatus CheckStatus,
    DateTimeOffset? LastCheckedUtc)
{
    /// <summary>The empty pre-check snapshot.</summary>
    public static AppUpdateSnapshot Empty { get; } = new("0.0.0", null, false, false, false,
        AppUpdateCheckStatus.NotChecked, null);
}

/// <summary>Thread-safe holder for the latest <see cref="AppUpdateSnapshot" />.</summary>
public interface IAppUpdateState
{
    AppUpdateSnapshot Current { get; }

    void Store(AppUpdateSnapshot snapshot);
}

/// <summary>Default lock-free in-memory update state.</summary>
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
