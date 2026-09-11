namespace XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     The daemon's health verdict for one container, in the engine's own vocabulary.
///     <para>
///         An unrecognised health string from the daemon maps to <see cref="None" /> and is logged once. That is not
///         the same as "no healthcheck": a service that declared one waits until its deadline and then fails, rather
///         than being treated as having no healthcheck at all.
///     </para>
/// </summary>
public enum ContainerHealthState
{
    /// <summary>No healthcheck is declared, or the daemon reported a state this engine does not recognise.</summary>
    None = 0,

    /// <summary>The healthcheck is inside its start period or has not yet succeeded.</summary>
    Starting = 1,

    /// <summary>The healthcheck is passing.</summary>
    Healthy = 2,

    /// <summary>The healthcheck failed its retry budget.</summary>
    Unhealthy = 3
}

/// <summary>What the daemon says a container is doing right now.</summary>
public sealed record ContainerRunState
{
    /// <summary>Whether the container is running.</summary>
    public required bool Running { get; init; }

    /// <summary>The daemon's own word for the state (<c>running</c>, <c>exited</c>, <c>created</c>, …), unmapped.</summary>
    public required string Status { get; init; }

    /// <summary>The exit code of the last run.</summary>
    public required long ExitCode { get; init; }

    /// <summary>Whether the kernel's OOM killer ended the last run — the difference between "the app crashed" and "the box ran out".</summary>
    public required bool OutOfMemoryKilled { get; init; }

    /// <summary>The health verdict.</summary>
    public required ContainerHealthState Health { get; init; }

    /// <summary>When the current or last run started, or null when the daemon reported its zero value.</summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>When the last run finished, or null when the daemon reported its zero value.</summary>
    public DateTimeOffset? FinishedAtUtc { get; init; }
}

/// <summary>One port the daemon actually bound. Distinct from the request side, whose host port may be unassigned.</summary>
public sealed record ContainerPublishedPort
{
    /// <summary>The port inside the container.</summary>
    public required int ContainerPort { get; init; }

    /// <summary>The protocol the daemon reported.</summary>
    public required string Protocol { get; init; }

    /// <summary>The host interface the daemon bound. Anything but <c>127.0.0.1</c> is a policy violation.</summary>
    public required string HostIp { get; init; }

    /// <summary>The host port the daemon bound. Always assigned: an unbound entry is not a published port.</summary>
    public required int HostPort { get; init; }
}

/// <summary>
///     One mount the daemon actually applied.
///     <para>
///         Distinct from the request-side <see cref="ContainerMount" /> because the effective set is not the requested
///         set: an image's own <c>VOLUME</c> instruction creates an anonymous volume that appears here and in no
///         request. That is exactly the mount an application-policy verifier has to reject, since it would put
///         application data outside the instance directory unnoticed.
///     </para>
/// </summary>
public sealed record ContainerMountView
{
    /// <summary>The daemon's mount type, <c>bind</c> or <c>volume</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The host path or volume name backing the mount.</summary>
    public required string Source { get; init; }

    /// <summary>The in-container path.</summary>
    public required string Destination { get; init; }

    /// <summary>Whether the mount is read-only inside the container.</summary>
    public required bool ReadOnly { get; init; }
}

/// <summary>
///     One row of a labelled container listing: enough to answer "is it still running, and if not why", in one call.
///     <para>
///         The bare id list the Development Mode client returns cannot distinguish a running container from an exited
///         one, so an observer built on it would report a crashed application as healthy.
///     </para>
/// </summary>
public sealed record ContainerSummary
{
    /// <summary>The container id.</summary>
    public required string Id { get; init; }

    /// <summary>The labels the daemon holds for it.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>
    ///     The daemon's own state word, lower-cased: <c>running</c>, <c>exited</c>, <c>created</c>, <c>paused</c>,
    ///     <c>restarting</c>, <c>removing</c> or <c>dead</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    ///     The exit code, or null when the daemon did not say.
    ///     <para>
    ///         Nullable because the list response carries no exit-code field: the value is read out of the daemon's
    ///         own status prose, so "absent" is a real answer and must never be read as <c>0</c>, which would report a
    ///         crashed application as a clean exit.
    ///     </para>
    /// </summary>
    public int? ExitCode { get; init; }
}

/// <summary>
///     What the daemon says a container actually is, read back after creation and again after start. This is the
///     evidence the application-container policy verifies against; "we passed the flag" is not verification.
///     <para>
///         It repeats the policy-relevant fields rather than embedding Development Mode's settings record: a verifier
///         reaching through two record types for half its evidence checks the wrong half one refactor later.
///     </para>
/// </summary>
public sealed record ContainerInspection
{
    /// <summary>The container id.</summary>
    public required string ContainerId { get; init; }

    /// <summary>The container name the daemon recorded.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     The image <em>reference</em> the container was created with — the string the caller pinned, digest and all.
    ///     <para>
    ///         Not the resolved image ID. The daemon reports both, and they are different answers to different
    ///         questions: the ID says which bytes are running, the reference says which bytes were asked for, and only
    ///         the second one can be compared against the digest-pinned reference an application manifest names. A
    ///         verifier handed the ID would find it matches nothing it holds.
    ///     </para>
    /// </summary>
    public required string Image { get; init; }

    /// <summary>The labels the daemon recorded.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }

    /// <summary>What the container is doing.</summary>
    public required ContainerRunState State { get; init; }

    /// <summary>
    ///     What the daemon was <em>asked</em> to bind, from the host configuration. Present on every inspect,
    ///     including before the container has started, and a blank host port means "daemon-assigned".
    /// </summary>
    public required IReadOnlyList<ContainerPortPublication> RequestedPortBindings { get; init; }

    /// <summary>
    ///     What the daemon actually bound, from the network settings. Empty before start, which is why the request
    ///     side above exists: the pre-start check and the post-start check read different fields.
    /// </summary>
    public required IReadOnlyList<ContainerPublishedPort> PublishedPorts { get; init; }

    /// <summary>The <c>uid:gid</c> the daemon recorded, or empty when it took the image's default. Read-back evidence only.</summary>
    public required string User { get; init; }

    /// <summary>The applied network mode.</summary>
    public required string NetworkMode { get; init; }

    /// <summary>Whether the container is privileged. Always a violation when true.</summary>
    public required bool Privileged { get; init; }

    /// <summary>Whether the root filesystem is read-only.</summary>
    public required bool ReadOnlyRootFilesystem { get; init; }

    /// <summary>The capabilities the daemon recorded as dropped.</summary>
    public required IReadOnlyList<string> CapabilitiesDropped { get; init; }

    /// <summary>The capabilities the daemon recorded as added.</summary>
    public required IReadOnlyList<string> CapabilitiesAdded { get; init; }

    /// <summary>The applied security options.</summary>
    public required IReadOnlyList<string> SecurityOptions { get; init; }

    /// <summary>The <em>effective</em> mount set, including anonymous volumes the image created.</summary>
    public required IReadOnlyList<ContainerMountView> Mounts { get; init; }

    /// <summary>Number of device mappings. The application policy requires zero.</summary>
    public required int DeviceCount { get; init; }

    /// <summary>The PID namespace mode; <c>host</c> is forbidden.</summary>
    public required string PidMode { get; init; }

    /// <summary>The IPC namespace mode; <c>host</c> is forbidden.</summary>
    public required string IpcMode { get; init; }

    /// <summary>The UTS namespace mode; <c>host</c> is forbidden.</summary>
    public required string UtsMode { get; init; }

    /// <summary>Applied memory ceiling in bytes; V1 verifies this is <c>0</c>.</summary>
    public required long MemoryBytes { get; init; }

    /// <summary>Applied CPU ceiling in nano-CPUs; V1 verifies this is <c>0</c>.</summary>
    public required long NanoCpus { get; init; }

    /// <summary>Applied process ceiling.</summary>
    public required long PidsLimit { get; init; }

    /// <summary>The restart policy the daemon recorded.</summary>
    public required ContainerRestartMode RestartMode { get; init; }

    /// <summary>The extra <c>/etc/hosts</c> entries the daemon recorded.</summary>
    public required IReadOnlyList<string> ExtraHosts { get; init; }
}
