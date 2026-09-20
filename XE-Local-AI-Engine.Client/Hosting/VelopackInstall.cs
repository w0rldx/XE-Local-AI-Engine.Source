namespace XE_Local_AI_Engine.Client.Hosting;

using Velopack.Locators;

/// <summary>
///     Resolves whether the process is running from a Velopack-managed install (installer OR portable).
/// </summary>
/// <remarks>
///     This is the signal the packaged desktop flavor uses to auto-enter desktop mode (see
///     <see cref="DesktopLaunch" />), because the Velopack stub launches the bare application exe without the
///     <c>XE_LAUNCH_MODE=desktop</c> env / <c>--desktop</c> arg a manual launcher would set. Kept separate from the
///     pure <see cref="DesktopLaunch" /> gate so the Velopack dependency stays out of the unit-tested decision logic.
/// </remarks>
internal static class VelopackInstall
{
    /// <summary>
    ///     <see langword="true" /> when the current locator reports a non-null installed version, which a raw-exe,
    ///     dev, Aspire or CI run never does (the off-flag invariant).
    /// </summary>
    /// <remarks>
    ///     <c>VelopackApp.Build().Run()</c> at the top of <c>Program.cs</c> establishes the process-wide
    ///     <see cref="VelopackLocator.Current" /> before this is read. The check mirrors <c>UpdateManager.IsInstalled</c>
    ///     and touches only local install metadata — no network, no update source, no GitHub token. With no bootstrap it
    ///     THROWS ("No VelopackLocator has been set") rather than returning null, the request-time state of every host
    ///     built without the entry point, so it is swallowed here once rather than at each call site.
    /// </remarks>
    internal static bool IsManaged()
    {
        try
        {
            return VelopackLocator.Current?.CurrentlyInstalledVersion is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
