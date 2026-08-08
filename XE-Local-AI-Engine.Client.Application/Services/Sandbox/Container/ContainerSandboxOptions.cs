namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

/// <summary>
///     Configuration for the Development Mode container sandbox (ADR 0004). Every value here is engine-owned: nothing
///     in this record may be supplied, influenced or overridden by a registered repository, because a repository is a
///     tree the agent can write and repository-supplied container configuration is rejected wholesale.
///     <para>
///         The defaults are chosen so that a container created from them satisfies the Docker hardening contract. The
///         validator rejects any combination that could not, rather than letting a weakened container be created and
///         only fail at read-back — a preflight failure is a better diagnostic than a create-then-reject.
///     </para>
/// </summary>
// A record rather than a plain options class so a caller can derive a variant with `with`. Configuration binding is
// unaffected: it uses the parameterless constructor and the init setters exactly as it would for a class.
public sealed record ContainerSandboxOptions
{
    public const string SectionName = "Development:ContainerSandbox";

    /// <summary>
    ///     Operator-approved, digest-pinned image reference — nothing else is permitted. A tag-only reference is
    ///     rejected by the validator: a tag is mutable, so it names whatever the registry last pushed rather than the
    ///     bytes the operator approved. Note that a digest pins image bytes and nothing more — mounts, runtime state,
    ///     host kernel, platform and dependency resolution all remain variable, so this is not reproducibility.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    ///     In-container UID the sandbox process runs as.
    ///     <para>
    ///         Null — the default — means "resolve it per create against the daemon that will run the container", which
    ///         is the only way to get this right: a rootful daemon maps an in-container UID straight through, so the
    ///         engine's own effective UID is the one that can use an engine-generated bind mount, while a rootless
    ///         daemon maps container UID 0 to the invoking user and every other UID into the subordinate range, which
    ///         owns nothing of ours. See <c>ResolvedContainerIdentity</c> for the measured evidence.
    ///     </para>
    ///     <para>
    ///         Set it only when your daemon maps identities in a way neither rule describes; an explicit value wins
    ///         over both. Zero is accepted here but is honoured ONLY against a daemon verified rootless — against a
    ///         rootful daemon it is host root, and the create is refused.
    ///     </para>
    /// </summary>
    [Range(0, int.MaxValue)]
    public int? UserId { get; init; }

    /// <summary>In-container GID. Null resolves per create, exactly as <see cref="UserId" /> does.</summary>
    [Range(0, int.MaxValue)]
    public int? GroupId { get; init; }

    /// <summary>Absolute in-container path the engine-generated workspace mount is bound at.</summary>
    [Required]
    public string WorkspaceMountTarget { get; init; } = "/workspace";

    /// <summary>
    ///     Absolute in-container path of the bounded <c>tmpfs</c> scratch area. The Docker hardening contract requires a read-only root
    ///     filesystem, so without a scratch mount nothing in the container can write anywhere at all.
    /// </summary>
    [Required]
    public string ScratchMountTarget { get; init; } = "/scratch";

    /// <summary>Size ceiling of the scratch <c>tmpfs</c>, in megabytes. Bounded so a runaway write cannot consume host RAM.</summary>
    [Range(1, 64 * 1024)]
    public int ScratchSizeMb { get; init; } = 512;

    /// <summary>
    ///     Absolute in-container path of the bounded <c>tmpfs</c> at the toolchain's fixed temporary directory.
    ///     <para>
    ///         This is not a convenience mount and it is not redundant with <see cref="ScratchMountTarget" />. The .NET
    ///         runtime backs a <em>named</em> <c>Mutex</c> with shared-memory files under a path it computes as
    ///         <c>/tmp/.dotnet/shm/session&lt;N&gt;</c>, and that path is a compile-time constant in the CoreCLR PAL
    ///         (<c>TEMP_DIRECTORY_PATH</c> in <c>palinternal.h</c>, consumed by <c>INIT_SharedFilesPath</c>). It does not
    ///         honour <c>TMPDIR</c>, <c>TMP</c> or <c>TEMP</c> — all three of which the engine already redirects to a
    ///         writable runtime mount — and dotnet/runtime#49822 closed the request to make it honour them as
    ///         <em>by design</em>: a global mutex needs a location every process agrees on, so it cannot be per-process
    ///         configurable. The <c>dotnet</c> CLI takes such a mutex on its first invocation, so with a read-only root
    ///         filesystem and no writable <c>/tmp</c>, every <c>dotnet</c> command fails <c>EROFS</c> before doing any
    ///         work. Measured: with this mount absent, restore, build and test all fail identically; with it present,
    ///         all three pass.
    ///     </para>
    ///     <para>
    ///         The mount must be at <c>/tmp</c> itself, not at the narrower <c>/tmp/.dotnet</c>. The PAL creates its
    ///         directory by <c>mkdtemp("/tmp/.dotnet.XXXXXX")</c> and renaming into place, so it needs the
    ///         <em>parent</em> writable; a tmpfs mounted precisely at <c>/tmp/.dotnet</c> was measured to fail with
    ///         <c>mkdtemp(…) == nullptr; errno == EROFS</c>.
    ///     </para>
    /// </summary>
    // S5443 flags "/tmp" because a publicly-writable directory on a HOST is a symlink/race hazard. This is not a host
    // path and never resolves on one: it is a mount target inside a container whose root filesystem is read-only, where
    // the only thing at this path is a private tmpfs this engine creates per container with noexec, nosuid, nodev and a
    // size bound. Nothing else has a handle to it, it does not survive the container, and the value is not a preference
    // — it is the path the .NET runtime compiles in, so "use a different directory" is not available. See the summary.
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "In-container tmpfs target under a read-only rootfs, not a host directory; the path is fixed by the .NET runtime.")]
    [Required]
    public string TempMountTarget { get; init; } = "/tmp";

    /// <summary>
    ///     Size ceiling of the temporary <c>tmpfs</c>, in megabytes. Deliberately far smaller than
    ///     <see cref="ScratchSizeMb" />: what lands here is shared-memory files and build-server sockets, and a full
    ///     restore, Release build and test run was measured to occupy 4 KB of it. The default leaves four orders of
    ///     magnitude of headroom while keeping the ceiling on the one mount whose contents nothing owns.
    /// </summary>
    [Range(1, 64 * 1024)]
    public int TempSizeMb { get; init; } = 64;

    /// <summary>Memory ceiling applied to the container, in megabytes.</summary>
    [Range(64, 1024 * 1024)]
    public int MemoryMb { get; init; } = 4096;

    /// <summary>CPU ceiling applied to the container, in whole or fractional cores.</summary>
    [Range(0.1, 1024d)]
    public double CpuCount { get; init; } = 2;

    /// <summary>Process/thread ceiling applied to the container.</summary>
    [Range(16, 1024 * 1024)]
    public int PidsLimit { get; init; } = 512;

    /// <summary>
    ///     Minimum Docker Engine API version the preflight accepts, as <c>major.minor</c>. 1.41 is the floor at which
    ///     every setting the Docker hardening contract requires is both settable and readable back from a container inspect.
    /// </summary>
    [Required]
    [RegularExpression(@"^\d+\.\d+$")]
    public string MinimumApiVersion { get; init; } = "1.41";

    /// <summary>Wall-clock budget for one daemon preflight round trip. Kept short: an operator is waiting on it.</summary>
    [Range(1, 300)]
    public int DaemonProbeTimeoutSeconds { get; init; } = 10;

    /// <summary>
    ///     Explicit daemon endpoint override. Null means "discover it" — <c>DOCKER_HOST</c> first, then the
    ///     platform defaults. Discovery order and the resolved source are reported to the operator by the preflight,
    ///     because a discovered endpoint is not the same thing as an approved one.
    /// </summary>
    public string? DaemonEndpoint { get; init; }
}
