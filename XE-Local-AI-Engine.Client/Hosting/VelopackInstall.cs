namespace XE_Local_AI_Engine.Client.Hosting;

using Velopack.Locators;

/// <summary>
///     Resolves whether the process is running from a Velopack-managed install (installer OR portable). This is the
///     signal the packaged desktop flavor uses to auto-enter desktop mode (see <see cref="DesktopLaunch" />), because the
///     Velopack stub launches the bare application exe without the <c>XE_LAUNCH_MODE=desktop</c> env / <c>--desktop</c>
///     arg a manual launcher would set. Kept separate from the pure <see cref="DesktopLaunch" /> gate so the Velopack
///     dependency stays out of the unit-tested decision logic.
/// </summary>
internal static class VelopackInstall
{
    /// <summary>
    ///     <see langword="true" /> when the process is running from a Velopack install — i.e. the current locator reports
    ///     a non-null installed version. A raw-exe / dev / Aspire / CI run is not a Velopack install, so the locator
    ///     reports no installed version and this returns <see langword="false" /> there (the off-flag invariant).
    /// </summary>
    /// <remarks>
    ///     <c>VelopackApp.Build().Run()</c> at the top of <c>Program.cs</c> establishes the process-wide
    ///     <see cref="VelopackLocator.Current" /> before this is read. The check mirrors Velopack's own
    ///     <c>UpdateManager.IsInstalled</c> (<c>Locator.CurrentlyInstalledVersion != null</c>) and touches only local
    ///     install metadata — no network, no update source, no GitHub token. Portable builds are installs too, so this is
    ///     <see langword="true" /> for both the installer and the portable layout.
    /// </remarks>
    internal static bool IsManaged()
    {
        return VelopackLocator.Current?.CurrentlyInstalledVersion is not null;
    }
}
