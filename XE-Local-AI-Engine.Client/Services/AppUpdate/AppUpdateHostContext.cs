namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Host facts the app-update services need but cannot derive themselves: whether the process is running in desktop
///     self-update mode, and the sanitized stable serve args to re-pass on relaunch so the new version comes back up in
///     the same local mode (re-binding the validated loopback port). Command and credential args are never retained.
/// </summary>
/// <param name="IsLocalMode"><see langword="true" /> when launched as the desktop self-update build.</param>
/// <param name="RestartArgs">The args to re-supply to the relaunched process after applying an update.</param>
public sealed record AppUpdateHostContext(bool IsLocalMode, IReadOnlyList<string> RestartArgs);
