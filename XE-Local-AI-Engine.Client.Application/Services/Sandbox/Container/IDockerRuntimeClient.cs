namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     The narrow slice of the Docker Engine API this product uses, expressed in this product's own types.
///     <para>
///         It exists so that <see cref="Implementation.DockerSandboxRuntimeProvider" /> can be tested against a client
///         that <em>lies</em>. The hardening contract is fail-closed on read-back — the provider must reject a container
///         whose settings did not take — and the only way to prove it rejects is to hand it an inspect result that
///         does not match what was asked for. A test cannot make a real daemon silently drop <c>--cap-drop ALL</c>, so
///         without this seam the fail-closed branch would be unreachable and therefore unverified.
///     </para>
/// </summary>
public interface IDockerRuntimeClient : IAsyncDisposable
{
    /// <summary>The endpoint this client was built against.</summary>
    DockerDaemonEndpoint Endpoint { get; }

    /// <summary>Ping the daemon and read its identity. Throws <see cref="DockerRuntimeException" /> on any failure.</summary>
    Task<DockerDaemonIdentity> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Create a container from <paramref name="specification" />, returning its id. Does not start it.</summary>
    Task<string> CreateContainerAsync(DockerContainerSpecification specification, CancellationToken cancellationToken = default);

    /// <summary>Start a created container.</summary>
    Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Read back the settings the daemon actually applied. This is the evidence hardening-contract verification runs on;
    ///     "we passed the flag" is not verification.
    /// </summary>
    Task<DockerContainerSettings> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Ids of every container — running or stopped — carrying <em>all</em> of <paramref name="labels" />.
    ///     <para>
    ///         Filtered daemon-side rather than listed-then-filtered here, because the difference is a security
    ///         property and not an optimisation: the caller removes what this returns, so a filter applied after the
    ///         fact would be one more place a foreign container could reach the removal loop.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<string>> ListContainersAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default);

    /// <summary>Force-remove a container. Best-effort by contract: a container that is already gone is not an error.</summary>
    Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>Run one command inside a running container and capture its bounded output.</summary>
    Task<DockerExecutionOutcome> ExecuteAsync(string containerId, DockerExecutionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
///     Everything the engine asks a container to be. Every field is engine-generated; none of it is derived from a
///     registered repository.
/// </summary>
public sealed record DockerContainerSpecification
{
    /// <summary>Digest-pinned image reference.</summary>
    public required string Image { get; init; }

    /// <summary>Container name, engine-generated and unique.</summary>
    public required string Name { get; init; }

    /// <summary>The <c>uid:gid</c> the container process runs as. Never root.</summary>
    public required string User { get; init; }

    /// <summary>In-container working directory.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Entrypoint of the long-lived container process the engine execs into.</summary>
    public required IReadOnlyList<string> Entrypoint { get; init; }

    /// <summary>Arguments to <see cref="Entrypoint" />.</summary>
    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>Docker network mode. The hardening contract requires this to be explicit, never defaulted.</summary>
    public required string NetworkMode { get; init; }

    /// <summary>Capabilities to drop. The hardening contract requires <c>ALL</c>.</summary>
    public required IReadOnlyList<string> CapabilitiesToDrop { get; init; }

    /// <summary>Security options. The hardening contract requires <c>no-new-privileges:true</c>.</summary>
    public required IReadOnlyList<string> SecurityOptions { get; init; }

    /// <summary>Whether the root filesystem is read-only. The hardening contract requires <see langword="true" />.</summary>
    public required bool ReadOnlyRootFilesystem { get; init; }

    /// <summary>Bounded <c>tmpfs</c> mounts, keyed by in-container path with the mount option string as the value.</summary>
    public required IReadOnlyDictionary<string, string> TemporaryFilesystems { get; init; }

    /// <summary>Engine-generated bind mounts. Repository-supplied mounts do not exist at this layer.</summary>
    public required IReadOnlyList<DockerBindMount> BindMounts { get; init; }

    /// <summary>Memory ceiling in bytes.</summary>
    public required long MemoryBytes { get; init; }

    /// <summary>CPU ceiling in nano-CPUs (1 core = 1_000_000_000).</summary>
    public required long NanoCpus { get; init; }

    /// <summary>Process/thread ceiling.</summary>
    public required long PidsLimit { get; init; }

    /// <summary>Provider-neutral labels used to identify engine-owned containers.</summary>
    public required IReadOnlyDictionary<string, string> Labels { get; init; }
}

/// <summary>One engine-generated bind mount. Mount propagation is explicit because the hardening contract requires it to be private.</summary>
public sealed record DockerBindMount
{
    /// <summary>Absolute host path.</summary>
    public required string HostPath { get; init; }

    /// <summary>Absolute in-container path.</summary>
    public required string ContainerPath { get; init; }

    /// <summary>Whether the mount is read-only inside the container.</summary>
    public required bool ReadOnly { get; init; }

    /// <summary>Mount propagation mode.</summary>
    public required string Propagation { get; init; }
}

/// <summary>
///     What the daemon says a container actually is, read back after creation. Shaped by what the hardening contract needs to check
///     rather than by the inspect payload: a field is here because a verification depends on it.
/// </summary>
public sealed record DockerContainerSettings
{
    /// <summary>The container id.</summary>
    public required string ContainerId { get; init; }

    /// <summary>The <c>uid:gid</c> the daemon recorded, or empty when it defaulted to root.</summary>
    public required string User { get; init; }

    /// <summary>The applied network mode.</summary>
    public required string NetworkMode { get; init; }

    /// <summary>Whether the container is privileged.</summary>
    public required bool Privileged { get; init; }

    /// <summary>Whether the root filesystem is read-only.</summary>
    public required bool ReadOnlyRootFilesystem { get; init; }

    /// <summary>The capabilities the daemon recorded as dropped.</summary>
    public required IReadOnlyList<string> CapabilitiesDropped { get; init; }

    /// <summary>The capabilities the daemon recorded as added. The hardening contract requires this to be empty.</summary>
    public required IReadOnlyList<string> CapabilitiesAdded { get; init; }

    /// <summary>The applied security options.</summary>
    public required IReadOnlyList<string> SecurityOptions { get; init; }

    /// <summary>The applied <c>tmpfs</c> mounts.</summary>
    public required IReadOnlyDictionary<string, string> TemporaryFilesystems { get; init; }

    /// <summary>The applied mounts.</summary>
    public required IReadOnlyList<DockerBindMount> Mounts { get; init; }

    /// <summary>Applied memory ceiling in bytes; 0 means unlimited.</summary>
    public required long MemoryBytes { get; init; }

    /// <summary>Applied CPU ceiling in nano-CPUs; 0 means unlimited.</summary>
    public required long NanoCpus { get; init; }

    /// <summary>Applied process ceiling; 0 or absent means unlimited.</summary>
    public required long PidsLimit { get; init; }

    /// <summary>Number of device mappings. The hardening contract requires zero.</summary>
    public required int DeviceCount { get; init; }

    /// <summary>The PID namespace mode; <c>host</c> is forbidden.</summary>
    public required string PidMode { get; init; }

    /// <summary>The IPC namespace mode; <c>host</c> is forbidden.</summary>
    public required string IpcMode { get; init; }

    /// <summary>The UTS namespace mode; <c>host</c> is forbidden.</summary>
    public required string UtsMode { get; init; }
}

/// <summary>One command to run inside a container.</summary>
public sealed record DockerExecutionRequest
{
    /// <summary>Executable to run.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>Optional in-container working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Optional environment additions for this command.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>
    ///     Optional standard input piped to the command, after which the write side is closed so the child sees EOF.
    ///     <para>
    ///         Load-bearing, not a convenience. Development Mode pipes patches to <c>git apply -</c>; a client that
    ///         dropped this would leave git reading EOF from an unattached stdin, and <c>git apply</c> given nothing
    ///         to apply <em>exits 0</em>. The caller would be told the patch applied while nothing changed, which is
    ///         a silent false green rather than a visible failure — so this field exists to make that impossible
    ///         rather than to make stdin available.
    ///     </para>
    /// </summary>
    public string? StandardInput { get; init; }

    /// <summary>Captured-output ceiling per stream, in bytes.</summary>
    public required int MaxCapturedBytes { get; init; }
}

/// <summary>The outcome of one in-container command.</summary>
public sealed record DockerExecutionOutcome
{
    /// <summary>Exit code.</summary>
    public required long ExitCode { get; init; }

    /// <summary>Captured standard output, truncated at the request's ceiling.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Captured standard error, truncated at the request's ceiling.</summary>
    public required string StandardError { get; init; }

    /// <summary>Whether standard-output bytes were discarded after the ceiling.</summary>
    public required bool StandardOutputTruncated { get; init; }

    /// <summary>Whether standard-error bytes were discarded after the ceiling.</summary>
    public required bool StandardErrorTruncated { get; init; }
}
