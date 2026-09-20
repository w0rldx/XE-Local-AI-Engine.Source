namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>What a live Docker daemon says it is.</summary>
/// <remarks>
///     <see cref="DaemonId" /> is the identity that matters: the engine generates it once at first start and keeps it for the life of that
///     installation, so it distinguishes "the same daemon at a new address" from "a different daemon", which an endpoint URI on its own
///     cannot do in either direction.
/// </remarks>
public sealed record DockerDaemonIdentity
{
    /// <summary>The daemon's own installation id, from the system-info endpoint.</summary>
    public required string DaemonId { get; init; }

    /// <summary>The Docker Engine version string, exactly as the daemon reports it.</summary>
    public required string ServerVersion { get; init; }

    /// <summary>The API version the daemon serves, as <c>major.minor</c>.</summary>
    public required string ApiVersion { get; init; }

    /// <summary>The oldest API version the daemon still accepts.</summary>
    public required string MinimumApiVersion { get; init; }

    /// <summary>The daemon's OS type (<c>linux</c> / <c>windows</c>), which decides what a mount even means.</summary>
    public required string OperatingSystem { get; init; }

    /// <summary>The endpoint this identity was read through.</summary>
    public required DockerDaemonEndpoint Endpoint { get; init; }

    /// <summary>Whether the daemon reported <c>name=rootless</c> among its security options.</summary>
    /// <remarks>
    ///     It changes which in-container UID maps to the engine's own host UID, and so which UID can use an engine-generated bind mount at
    ///     all: a rootless daemon maps container UID 0 to the invoking user and <c>N&gt;0</c> to <c>subuid_base + N - 1</c>, making the
    ///     conventional non-root UID a host account that owns nothing of ours. Reported rather than acted on here — it is an input to the
    ///     identity decision, and the probe after container creation proves the mapping.
    /// </remarks>
    public bool IsRootless { get; init; }

    /// <summary>Whether the daemon reported <c>name=seccomp</c> among its security options.</summary>
    /// <remarks>
    ///     Load-bearing rather than informational: a daemon with seccomp compiled out or disabled still ACCEPTS a <c>seccomp=</c> option
    ///     and records it in the container's <c>HostConfig</c>, so the create-time read-back cannot tell it from a confining one. This
    ///     flag is the only place that difference is visible, which is why the preflight refuses a daemon that does not report it rather
    ///     than creating a container whose confinement it cannot establish. It defaults to <see langword="false" /> for that reason.
    /// </remarks>
    public bool SupportsSeccomp { get; init; }
}
