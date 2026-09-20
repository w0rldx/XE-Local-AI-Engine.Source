namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>Provider-neutral capability flags advertised by an <see cref="ISandboxRuntimeProvider" />.</summary>
/// <remarks>
///     AgentHome reads these to gate optional behaviour — read-only mounts, network policy, resource limits — and to skip operations a
///     provider cannot serve. No provider SDK informs this enum.
/// </remarks>
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
    ///     (<see cref="SandboxIsolationMode.Filesystem" />).
    /// </summary>
    /// <remarks>
    ///     Advertised only where a probe has EXERCISED the real chain and confirmed its positive controls, never on the strength of a
    ///     binary being installed.
    /// </remarks>
    SupportsFilesystemIsolation = 1 << 9,

    /// <summary>
    ///     Commands run against the HOST's compilers, SDKs and interpreters, as the engine's user sees them
    ///     (<see cref="SandboxToolchainSource.HostToolchain" />).
    /// </summary>
    SuppliesHostToolchain = 1 << 10,

    /// <summary>
    ///     Commands run against a digest-pinned, operator-approved image the engine names
    ///     (<see cref="SandboxToolchainSource.EngineApprovedImage" />).
    /// </summary>
    /// <remarks>
    ///     The axis ADR 0007 Decision 5 added so that the flags a backend advertises and the axes a workload declares are ONE vocabulary;
    ///     without it the need that drove ADR 0004 was the only one in this engine that could not be written down. A provider advertises
    ///     exactly one of this and <see cref="SuppliesHostToolchain" />, and they are not alternatives a caller may fall back between: a
    ///     workload needing the host's SDK is not served by an image pinned to a different one, and a repository needing .NET 8 on a
    ///     .NET 10 host is why the image exists at all.
    /// </remarks>
    SuppliesImageToolchain = 1 << 11,

    /// <summary>
    ///     Commands cannot see the host filesystem: the sandbox's view contains only what the engine put there. This is the PROPERTY;
    ///     <see cref="SupportsFilesystemIsolation" /> is one MECHANISM for it.
    /// </summary>
    /// <remarks>
    ///     They are deliberately not one flag: that one means "can serve <see cref="SandboxIsolationMode.Filesystem" />", a specific
    ///     create-request contract of named read-only trees bound at host paths, a synthetic <c>/etc</c> and one writable jail. A container
    ///     has the property and implements none of the contract, so folding them together would either lie to <c>run_python</c> or refuse a
    ///     container the floor it genuinely provides. Advertised only on evidence: the process backend where its probe exercised the
    ///     bubblewrap chain, the container backend because every create reads its settings back and fails closed on mismatch.
    /// </remarks>
    SupportsHostFilesystemBoundary = 1 << 12
}
