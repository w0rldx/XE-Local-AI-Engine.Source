namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     One engine-generated mount request: "make this host path visible inside the sandbox".
///     <para>
///         Provider-neutral by construction, and it has to be. The engine knows which host directories a build needs
///         (HOME, temp, the package cache, the tool state root); it does not, and must not, know how a given provider
///         makes them reachable. A container binds them; a host-process jail already sees them and does nothing at all.
///         Putting a container mount type on the create request would leak a Docker concept into the contract every
///         other provider implements — which <c>SandboxContractGuardTests</c> fails the build for, correctly.
///     </para>
///     <para>
///         <see cref="SandboxPath" /> is a <em>requested</em> target, not a promise. A provider is free to place the
///         mount elsewhere — the process provider necessarily does, because a host child sees the host path and nothing
///         else — so the resolved answer is read back off <see cref="SandboxHandle.Mounts" /> rather than assumed here.
///     </para>
/// </summary>
public sealed record SandboxMount
{
    /// <summary>Absolute host path to expose. Must exist before the sandbox is created.</summary>
    public required string HostPath { get; init; }

    /// <summary>
    ///     The absolute in-sandbox path the caller would like this to appear at, expressed POSIX-style. Providers that
    ///     cannot honour it (the process jail) report what they did instead; providers that can (the container) use it
    ///     verbatim after validating it against every other mount target.
    /// </summary>
    public required string SandboxPath { get; init; }

    /// <summary>
    ///     Whether the mount must be read-only inside the sandbox.
    ///     <para>
    ///         Capability-gated on the caller's side: a provider that does not advertise
    ///         <see cref="SandboxProviderCapabilities.SupportsReadOnlyMounts" /> rejects this fail-closed rather than
    ///         quietly serving a writable mount, so a caller must ask for read-only only when the flag is advertised.
    ///         The alternative — silently downgrading — is exactly the failure the whole capability contract exists to
    ///         prevent.
    ///     </para>
    /// </summary>
    public bool ReadOnly { get; init; }
}

/// <summary>
///     What a mount actually became, reported back on <see cref="SandboxHandle" /> so a caller can answer "what is this
///     host path called inside the sandbox?" without knowing which provider answered.
/// </summary>
/// <param name="HostPath">The canonical host path that was requested.</param>
/// <param name="SandboxPath">The path that names those same bytes from inside the sandbox.</param>
/// <param name="ReadOnly">Whether the mount is read-only inside the sandbox.</param>
public sealed record SandboxMountBinding(string HostPath, string SandboxPath, bool ReadOnly);
