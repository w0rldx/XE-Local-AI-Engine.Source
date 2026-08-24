namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Provider-neutral capability flags advertised by an <see cref="ISandboxRuntimeProvider" />. AgentHome reads
///     these to gate optional behavior (read-only mounts, network policy, resource limits) and
///     to skip operations a provider cannot serve. No provider SDK informs this enum.
/// </summary>
[Flags]
public enum SandboxProviderCapabilities
{
    None = 0,
    SupportsCopyInto = 1 << 0,
    SupportsCopyOut = 1 << 1,
    SupportsReadOnlyMounts = 1 << 2,
    SupportsNetworkPolicy = 1 << 3,
    SupportsResourceLimits = 1 << 4,
    SupportsCommandCancellation = 1 << 5,
    SupportsAttach = 1 << 6,
    SupportsKill = 1 << 7,
    SupportsTrustedHostWorkspace = 1 << 8,

    /// <summary>
    ///     The provider can run a command with the host filesystem absent from its mount namespace
    ///     (<see cref="SandboxIsolationMode.Filesystem" />). Advertised only where a probe has EXERCISED the real
    ///     chain and confirmed its positive controls, never on the strength of a binary being installed.
    /// </summary>
    SupportsFilesystemIsolation = 1 << 9
}
