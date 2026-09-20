namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

/// <summary>Configuration for the Development Mode container sandbox (ADR 0004).</summary>
/// <remarks>
///     Every value here is engine-owned: nothing may be supplied, influenced or overridden by a registered repository, because a
///     repository is a tree the agent can write and repository-supplied container configuration is rejected wholesale. The defaults are
///     chosen so a container created from them satisfies the Docker hardening contract, and the validator rejects any combination that
///     could not rather than letting a weakened container be created and fail only at read-back.
/// </remarks>
// A record rather than a plain options class so a caller can derive a variant with `with`. Configuration binding is
// unaffected: it uses the parameterless constructor and the init setters exactly as it would for a class.
public sealed record ContainerSandboxOptions
{
    public const string SectionName = "Development:ContainerSandbox";

    /// <summary>Operator-approved, digest-pinned image reference — nothing else is permitted.</summary>
    /// <remarks>
    ///     A tag-only reference is rejected by the validator: a tag is mutable, naming whatever the registry last pushed rather than the
    ///     bytes the operator approved. A digest pins image bytes and nothing more — mounts, runtime state, host kernel, platform and
    ///     dependency resolution all remain variable — so this is not reproducibility.
    /// </remarks>
    public string? Image { get; init; }

    /// <summary>In-container UID the sandbox process runs as; null resolves it per create against the daemon that will run it.</summary>
    /// <remarks>
    ///     Resolving per create is the only way to get this right: a rootful daemon maps an in-container UID straight through, so the
    ///     engine's own effective UID is the one that can use an engine-generated bind mount, while a rootless daemon maps container UID 0
    ///     to the invoking user and every other UID into the subordinate range, which owns nothing of ours
    ///     (<c>ResolvedContainerIdentity</c> carries the measured evidence). Set it only for a daemon neither rule describes; an explicit
    ///     value wins over both. Zero is honoured ONLY against a daemon verified rootless, being host root otherwise.
    /// </remarks>
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

    /// <summary>Absolute in-container path of the bounded <c>tmpfs</c> at the toolchain's fixed temporary directory.</summary>
    /// <remarks>
    ///     Neither a convenience nor redundant with <see cref="ScratchMountTarget" />: the .NET runtime backs a NAMED mutex with
    ///     shared-memory files under <c>/tmp/.dotnet/shm/…</c>, a compile-time constant in the CoreCLR PAL that honours no <c>TMPDIR</c>,
    ///     by design, a global mutex needing a location every process agrees on. The <c>dotnet</c> CLI takes such a mutex on its first
    ///     invocation, so with a read-only root and no writable <c>/tmp</c> every command fails <c>EROFS</c> before doing any work —
    ///     measured. It must be at <c>/tmp</c> itself, the PAL needing the PARENT writable for its <c>mkdtemp</c>-and-rename.
    /// </remarks>
    // S5443 flags "/tmp" because a publicly-writable directory on a HOST is a race hazard. This never resolves on a host: it is a mount
    // target inside a read-only-rooted container, holding only a private per-container tmpfs with noexec, nosuid, nodev and a size bound.
    [SuppressMessage("Security Hotspot",
        "S5443:Using publicly writable directories is security-sensitive",
        Justification = "In-container tmpfs target under a read-only rootfs, not a host directory; the path is fixed by the .NET runtime.")]
    [Required]
    public string TempMountTarget { get; init; } = "/tmp";

    /// <summary>Size ceiling of the temporary <c>tmpfs</c>, in megabytes, deliberately far smaller than <see cref="ScratchSizeMb" />.</summary>
    /// <remarks>
    ///     What lands here is shared-memory files and build-server sockets, and a full restore, Release build and test run was measured to
    ///     occupy 4 KB. The default leaves four orders of magnitude of headroom while keeping a ceiling on the one mount nothing owns.
    /// </remarks>
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

    /// <summary>Explicit daemon endpoint override; null discovers it, <c>DOCKER_HOST</c> first and then the platform defaults.</summary>
    /// <remarks>
    ///     Discovery order and the resolved source are reported to the operator by the preflight, because a discovered endpoint is not the
    ///     same thing as an approved one.
    /// </remarks>
    public string? DaemonEndpoint { get; init; }
}
