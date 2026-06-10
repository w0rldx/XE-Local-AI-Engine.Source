namespace XE_Local_AI_Engine.Installer.Driver.Windows;

/// <summary>
///     Writes the host-agent runtime configuration during the <c>config-write</c> phase (plan §7.5):
///     the runtime manifest/runtime.json under <c>%ProgramData%\XE-Local-AI-Engine\host-agent\</c> and
///     the DPAPI-protected admin token. Abstracted so the driver's transport logic is testable
///     cross-platform and the single Windows-only API (DPAPI) is isolated behind one guarded type.
/// </summary>
public interface IInstallerHostConfigWriter
{
    Task WriteAsync(string bundlePath, CancellationToken cancellationToken = default);
}
