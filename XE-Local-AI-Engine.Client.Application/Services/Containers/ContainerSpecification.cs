namespace XE_Local_AI_Engine.Client.Services.Containers;

using System.Text;

/// <summary>
///     How the engine restarts an application container. Two values, and the omission is the point: a runtime able to
///     express Docker's <c>always</c> would let a later caller invent a restart policy ADR 0010 did not authorise.
/// </summary>
public enum ContainerRestartMode
{
    /// <summary>The daemon never restarts the container. Test-only in V1; kept so the runtime can express "no policy".</summary>
    None = 0,

    /// <summary>
    ///     The daemon restarts the container unless a person stopped it. Set once at creation and never changed, so a
    ///     container the user stopped stays stopped across a daemon restart and an installed application survives an
    ///     engine that is not running.
    /// </summary>
    UnlessStopped = 1
}

/// <summary>
///     Everything the engine asks an application container to be.
///     <para>
///         A new record rather than new fields on <c>DockerContainerSpecification</c>. Every property there is
///         <c>required</c>, and <c>DockerSandboxHardening.FindViolations</c> reads that record as the complete
///         statement of what must be true about a Development Mode container; optional properties added for a second
///         consumer would be fields its verifier does not check, which is how a hardening contract quietly stops
///         covering the thing it names.
///     </para>
/// </summary>
public sealed record ContainerSpecification
{
    /// <summary>Digest-pinned image reference. Must contain <c>@sha256:</c>; the runtime refuses a tag before any wire call.</summary>
    public required string Image { get; init; }

    /// <summary>Engine-generated container name.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     The <c>uid:gid</c> to run as, or <see langword="null" /> for the image's own default user.
    ///     <para>
    ///         Null in production (ADR 0010 §6): the curated images start as in-container root and drop privileges
    ///         through their own entrypoints, and forcing a uid breaks them. A blank string is refused rather than
    ///         read as null, because "" and null would otherwise be the same instruction written two ways and only one
    ///         of them says what it means.
    ///     </para>
    /// </summary>
    public string? User { get; init; }

    /// <summary>Engine-owned labels identifying the owner, install, instance and service.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>The container's environment, including values decrypted from the instance's stored variables.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>Engine-generated bind mounts under the instance directory. Named volumes do not exist at this layer.</summary>
    public required IReadOnlyList<ContainerMount> Mounts { get; init; }

    /// <summary>Ports to publish. Every entry binds to loopback; the runtime refuses anything else before the wire call.</summary>
    public required IReadOnlyList<ContainerPortPublication> PublishedPorts { get; init; }

    /// <summary>Capabilities to drop. The application policy requires <c>ALL</c>.</summary>
    public required IReadOnlyList<string> CapabilitiesToDrop { get; init; }

    /// <summary>Capabilities to add back, drawn only from Docker's own default set.</summary>
    public required IReadOnlyList<string> CapabilitiesToAdd { get; init; }

    /// <summary>Security options; the application policy requires <c>no-new-privileges:true</c> and the engine seccomp profile.</summary>
    public required IReadOnlyList<string> SecurityOptions { get; init; }

    /// <summary>Whether the root filesystem is read-only. Writable unless the manifest asks otherwise.</summary>
    public required bool ReadOnlyRootFilesystem { get; init; }

    /// <summary>The engine-created instance network this container joins. Exactly one, set at create time.</summary>
    public required string NetworkName { get; init; }

    /// <summary>DNS aliases on that network — how one service of a multi-service application finds another.</summary>
    public required IReadOnlyList<string> NetworkAliases { get; init; }

    /// <summary>The restart policy, applied once at creation.</summary>
    public required ContainerRestartMode RestartMode { get; init; }

    /// <summary>
    ///     Memory ceiling in bytes; <c>0</c> means unlimited, and V1 always passes <c>0</c>. The manifest's memory
    ///     figures are admission-gate inputs, not per-container limits. The field stays so a later ruling needs no
    ///     contract change and so the read-back verifier can assert the zero.
    /// </summary>
    public required long MemoryBytes { get; init; }

    /// <summary>CPU ceiling in nano-CPUs; <c>0</c> means unlimited, and V1 always passes <c>0</c>.</summary>
    public required long NanoCpus { get; init; }

    /// <summary>Process/thread ceiling, applied per container. Unlike the two above, this one is really imposed.</summary>
    public required long PidsLimit { get; init; }

    /// <summary>Entrypoint override, or null to use the image's.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }

    /// <summary>Command override, or null to use the image's.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    /// <summary>Working directory override, or null to use the image's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra <c>/etc/hosts</c> entries, such as <c>host.docker.internal:host-gateway</c>.</summary>
    public IReadOnlyList<string> ExtraHosts { get; init; } = [];

    /// <summary>The container's healthcheck, or null when the service declares none.</summary>
    public ContainerHealthcheck? Healthcheck { get; init; }

    // The generated ToString() prints every property, and Environment holds values decrypted from the instance's
    // stored variables — an application's admin password among them. One LogDebug("{Spec}", spec) downstream would
    // therefore write that password to the node log. Suppressing the printer makes that impossible rather than merely
    // forbidden, and ContainerRuntimeRecordPrintingTests asserts a known secret cannot appear.
    //
    // `private bool PrintMembers(StringBuilder)` and never `protected override`: on a sealed record whose base is
    // object the compiler expects exactly this signature, and the override form does not compile here.
    //
    // The compiler's shape is also why four analyzers have to be silenced rather than obeyed: the printer is unused,
    // instance-free and unimplemented BY DESIGN, and every fix they suggest — making it static, dropping the
    // parameter — changes the signature into one the compiler no longer recognises as the record's printer, which
    // silently restores the ToString() that prints the secrets.
#pragma warning disable CA1822, S2325, S1172, IDE0060
    private bool PrintMembers(StringBuilder builder)
    {
        return false;
    }
#pragma warning restore CA1822, S2325, S1172, IDE0060
}

/// <summary>One published port. The host side is always loopback; nothing at this layer can express a LAN binding.</summary>
public sealed record ContainerPortPublication
{
    /// <summary>The port inside the container.</summary>
    public required int ContainerPort { get; init; }

    /// <summary>
    ///     The host interface to bind. Only <c>127.0.0.1</c> is accepted — not <c>::1</c>, which is a socket this
    ///     node's own callers never connect to — and the check happens before the parameters are built:
    ///     <c>required</c> means "assigned", not "non-empty", and an empty host IP makes Docker bind <c>0.0.0.0</c>.
    /// </summary>
    public required string HostIp { get; init; }

    /// <summary>The host port, or null to let the daemon assign one. Null is legal before start and a violation after it.</summary>
    public int? HostPort { get; init; }

    /// <summary>The protocol; <c>tcp</c> unless a manifest says otherwise.</summary>
    public string Protocol { get; init; } = "tcp";
}

/// <summary>One engine-generated bind mount, as requested. What the daemon actually applied is a <see cref="ContainerMountView" />.</summary>
public sealed record ContainerMount
{
    /// <summary>Absolute host path, always under the instance directory.</summary>
    public required string HostPath { get; init; }

    /// <summary>Absolute in-container path.</summary>
    public required string ContainerPath { get; init; }

    /// <summary>Whether the mount is read-only inside the container.</summary>
    public required bool ReadOnly { get; init; }

    /// <summary>Mount propagation, explicit because a defaulted one is not something a read-back can verify.</summary>
    public string Propagation { get; init; } = "private";
}

/// <summary>A container healthcheck, in the daemon's own terms.</summary>
public sealed record ContainerHealthcheck
{
    /// <summary>The test, in Docker's array form (for example <c>["CMD-SHELL", "curl -f http://localhost/ || exit 1"]</c>).</summary>
    public required IReadOnlyList<string> Test { get; init; }

    /// <summary>How often the test runs.</summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>How long one run may take before it counts as failed.</summary>
    public required TimeSpan Timeout { get; init; }

    /// <summary>Consecutive failures before the container is reported unhealthy.</summary>
    public required int Retries { get; init; }

    /// <summary>Grace period after start during which a failure does not count against <see cref="Retries" />.</summary>
    public required TimeSpan StartPeriod { get; init; }
}
