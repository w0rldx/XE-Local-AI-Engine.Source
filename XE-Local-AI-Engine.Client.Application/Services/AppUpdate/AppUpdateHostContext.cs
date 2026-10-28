namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Host facts the app-update services need but cannot derive themselves: whether the process is running in desktop
///     self-update mode, and the sanitized stable serve args to re-pass on relaunch.
/// </summary>
/// <remarks>
///     The args bring the new version back up in the same local mode, re-binding the validated loopback port. Command
///     and credential args are never retained.
/// </remarks>
public sealed class AppUpdateHostContext
{
    /// <summary><see langword="true" /> when launched as the desktop self-update build.</summary>
    public required bool IsLocalMode { get; init; }

    public bool IsShellOwned { get; init; }

    public string? DataDirectory { get; init; }

    /// <summary>The args to re-supply to the relaunched process after applying an update.</summary>
    public required IReadOnlyList<string> RestartArgs { get; init; }
}
