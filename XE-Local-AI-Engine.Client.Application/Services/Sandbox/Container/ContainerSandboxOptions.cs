namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.ComponentModel.DataAnnotations;

/// <summary>
///     Configuration for the Development Mode container sandbox (ADR 0004). Every value here is engine-owned: nothing
///     in this record may be supplied, influenced or overridden by a registered repository, because a repository is a
///     tree the agent can write and decision D7 rejects repository-supplied container configuration wholesale.
///     <para>
///         The defaults are chosen so that a container created from them satisfies the §3.8 hardening contract. The
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
    ///     Operator-approved, digest-pinned image reference (D7 permits nothing else). A tag-only reference is
    ///     rejected by the validator: a tag is mutable, so it names whatever the registry last pushed rather than the
    ///     bytes the operator approved. Note that a digest pins image bytes and nothing more — mounts, runtime state,
    ///     host kernel, platform and dependency resolution all remain variable, so this is not reproducibility.
    /// </summary>
    public string? Image { get; init; }

    /// <summary>
    ///     In-container UID the sandbox process runs as. Must be non-zero: §3.8 requires non-root execution with an
    ///     explicit UID/GID, and Docker runs as root unless overridden.
    ///     <para>
    ///         Null means "resolve the engine process's own effective UID at startup". That is the value that makes an
    ///         engine-generated bind mount readable and writable under a conventional rootful daemon, where an
    ///         in-container UID maps straight through to the same host UID.
    ///     </para>
    /// </summary>
    [Range(1, int.MaxValue)]
    public int? UserId { get; init; }

    /// <summary>In-container GID. Must be non-zero. Null resolves to the engine process's effective GID.</summary>
    [Range(1, int.MaxValue)]
    public int? GroupId { get; init; }

    /// <summary>Absolute in-container path the engine-generated workspace mount is bound at.</summary>
    [Required]
    public string WorkspaceMountTarget { get; init; } = "/workspace";

    /// <summary>
    ///     Absolute in-container path of the bounded <c>tmpfs</c> scratch area. §3.8 requires a read-only root
    ///     filesystem, so without a scratch mount nothing in the container can write anywhere at all.
    /// </summary>
    [Required]
    public string ScratchMountTarget { get; init; } = "/scratch";

    /// <summary>Size ceiling of the scratch <c>tmpfs</c>, in megabytes. Bounded so a runaway write cannot consume host RAM.</summary>
    [Range(1, 64 * 1024)]
    public int ScratchSizeMb { get; init; } = 512;

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
    ///     every setting §3.8 requires is both settable and readable back from a container inspect.
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
    ///     because under D10 a discovered endpoint is not the same thing as an approved one.
    /// </summary>
    public string? DaemonEndpoint { get; init; }
}
