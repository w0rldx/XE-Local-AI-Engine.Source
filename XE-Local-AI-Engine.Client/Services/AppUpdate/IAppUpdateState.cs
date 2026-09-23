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

    /// <summary>The channel this node follows now: the operator's stored choice, or the baked default.</summary>
    /// <remarks>
    ///     Deliberately NOT <c>required</c>, with a default, unlike the seven members above. The service's private
    ///     snapshot factory is the only producer, and making these required would churn every construction site in
    ///     the tests for no safety gain.
    /// </remarks>
    public AppUpdateChannel SelectedChannel { get; init; } = AppUpdateChannel.Stable;

    /// <summary>The channel baked into this artifact; what an unset choice resolves to.</summary>
    public AppUpdateChannel DefaultChannel { get; init; } = AppUpdateChannel.Stable;

    /// <summary>The newest stable version on the main feed, or null when it holds none or no feed was read.</summary>
    public string? RecommendedVersion { get; init; }

    /// <summary>
    ///     The LOWEST channel that also offers <see cref="AvailableVersion" />; null when no update is offered.
    /// </summary>
    public AppUpdateChannel? AvailableChannel { get; init; }

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
