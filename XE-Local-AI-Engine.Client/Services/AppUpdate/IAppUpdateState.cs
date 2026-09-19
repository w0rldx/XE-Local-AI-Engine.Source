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
public sealed record AppUpdateSnapshot
{
    public required string CurrentVersion { get; init; }

    public required string? AvailableVersion { get; init; }

    public required bool UpdateAvailable { get; init; }

    public required bool IsConfigured { get; init; }

    public required bool IsDesktop { get; init; }

    public required AppUpdateCheckStatus CheckStatus { get; init; }

    public required DateTimeOffset? LastCheckedUtc { get; init; }

    /// <summary>The empty pre-check snapshot.</summary>
    public static AppUpdateSnapshot Empty { get; } = new()
    {
        CurrentVersion = "0.0.0",
        AvailableVersion = null,
        UpdateAvailable = false,
        IsConfigured = false,
        IsDesktop = false,
        CheckStatus = AppUpdateCheckStatus.NotChecked,
        LastCheckedUtc = null
    };
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
