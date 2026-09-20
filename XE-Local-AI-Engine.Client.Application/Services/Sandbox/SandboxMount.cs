namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>One engine-generated mount request: "make this host path visible inside the sandbox".</summary>
/// <remarks>
///     Provider-neutral by construction: the engine knows which host directories a build needs (HOME, temp, the package cache, the tool
///     state root) and must not know how a provider makes them reachable — a container binds them, a host-process jail already sees them.
///     A container mount type on the create request would leak a Docker concept into the contract every provider implements, which
///     <c>SandboxContractGuardTests</c> fails the build for. <see cref="SandboxPath" /> is a REQUESTED target, not a promise; the resolved
///     answer is read back off <see cref="SandboxHandle.Mounts" />.
/// </remarks>
public sealed record SandboxMount
{
    /// <summary>Absolute host path to expose. Must exist before the sandbox is created.</summary>
    public required string HostPath { get; init; }

    /// <summary>The absolute in-sandbox path the caller would like this to appear at, expressed POSIX-style.</summary>
    /// <remarks>
    ///     A provider that cannot honour it (the process jail) reports what it did instead; one that can (the container) uses it verbatim
    ///     after validating it against every other mount target.
    /// </remarks>
    public required string SandboxPath { get; init; }

    /// <summary>
    ///     Whether <see cref="SandboxPath" /> names a path RELATIVE TO THE TRUSTED HOST WORKSPACE ROOT rather than a target the provider
    ///     derives from <see cref="HostPath" />.
    /// </summary>
    /// <remarks>
    ///     Derivation expresses "put THIS file where it already is", never "put file A where file B is"; one case needs that — shadowing a
    ///     committed credential with engine-generated empty content, whose mount SOURCE must live outside the workspace so the real file is
    ///     left byte-unchanged. A separate opt-in because the per-task HOME, temp and package roots sit outside the workspace and must stay
    ///     there: reading every target as workspace-relative would put the package cache in the work tree. A provider with no mount layer
    ///     ignores it, so a caller asks only where read-only mounts are advertised.
    /// </remarks>
    public bool TargetIsWorkspaceRelative { get; init; }

    /// <summary>Whether the mount must be read-only inside the sandbox.</summary>
    /// <remarks>
    ///     Capability-gated on the caller's side: a provider not advertising
    ///     <see cref="SandboxProviderCapabilities.SupportsReadOnlyMounts" /> rejects this fail-closed rather than quietly serving a
    ///     writable mount, so a caller asks for read-only only where the flag is advertised. Silently downgrading is exactly the failure
    ///     the capability contract exists to prevent.
    /// </remarks>
    public bool ReadOnly { get; init; }
}

/// <summary>
///     What a mount actually became, reported back on <see cref="SandboxHandle" /> so a caller can answer "what is this host path called
///     inside the sandbox?" without knowing which provider answered.
/// </summary>
public sealed class SandboxMountBinding
{
    /// <summary>The canonical host path that was requested.</summary>
    public required string HostPath { get; init; }

    /// <summary>The path that names those same bytes from inside the sandbox.</summary>
    public required string SandboxPath { get; init; }

    /// <summary>Whether the mount is read-only inside the sandbox.</summary>
    public required bool ReadOnly { get; init; }
}
