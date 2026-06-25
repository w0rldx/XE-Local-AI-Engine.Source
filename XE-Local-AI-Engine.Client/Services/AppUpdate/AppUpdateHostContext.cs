namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>
///     Host facts the app-update services need but cannot derive themselves: whether the process is running in desktop
///     self-update mode, and the args to re-pass on relaunch so the new version comes back up in desktop mode (re-binding
///     the persisted loopback port). Registered as a singleton from <c>Program.cs</c>, where the desktop flag + process
///     args are known. Off the desktop flag the update services and endpoints are not registered at all.
/// </summary>
/// <param name="IsDesktop"><see langword="true" /> when launched as the desktop self-update build.</param>
/// <param name="RestartArgs">The args to re-supply to the relaunched process after applying an update.</param>
public sealed record AppUpdateHostContext(bool IsDesktop, IReadOnlyList<string> RestartArgs);
