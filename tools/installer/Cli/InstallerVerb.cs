namespace XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     The four verbs the installer CLI exposes. <see cref="Install" />, <see cref="Reset" />,
///     <see cref="Remove" /> mutate the machine; <see cref="Status" /> is read-only.
/// </summary>
public enum InstallerVerb
{
    Install,
    Reset,
    Remove,
    Status
}
