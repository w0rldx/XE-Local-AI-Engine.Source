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
    SupportsFilesystemIsolation = 1 << 9,

    /// <summary>
    ///     Commands run against the HOST's compilers, SDKs and interpreters, as the engine's user sees them
    ///     (<see cref="SandboxToolchainSource.HostToolchain" />).
    /// </summary>
    SuppliesHostToolchain = 1 << 10,

    /// <summary>
    ///     Commands run against a digest-pinned, operator-approved image the engine names
    ///     (<see cref="SandboxToolchainSource.EngineApprovedImage" />). The axis ADR 0007 Decision 5 added so that the
    ///     flags a backend advertises and the axes a workload declares are ONE vocabulary — without it, the need that
    ///     drove ADR 0004 was the only need in this engine that could not be written down.
    ///     <para>
    ///         A provider advertises exactly one of this and <see cref="SuppliesHostToolchain" />. They are not
    ///         alternatives a caller may fall back between: a workload that needs the host's SDK is not served by an
    ///         image pinned to a different one, and a repository needing .NET 8 on a .NET 10 host is the reason the
    ///         image exists at all.
    ///     </para>
    /// </summary>
    SuppliesImageToolchain = 1 << 11
}
