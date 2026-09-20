namespace XE_Local_AI_Engine.Client.Services.Containers;

using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>The engine-owned container runtime: everything ADR 0010's application containers need from a container engine, and nothing else.</summary>
/// <remarks>
///     It derives from <see cref="IDockerRuntimeClient" /> rather than standing beside it, so there is one transport
///     and one lying fake; Development Mode's audited surface does not widen, because every new member is declared
///     here and <c>DockerSandboxRuntimeProvider</c> takes the base one. The names are new rather than OVERLOADS on
///     purpose: overloading across a base and a derived interface resolves on the static type of the reference, so a
///     caller holding the base type would silently get Development Mode's create.
/// </remarks>
public interface IContainerRuntime : IDockerRuntimeClient
{
    /// <summary>Create a container from <paramref name="specification" /> and return its id, WITHOUT starting it, so the caller keeps its create, verify-read-back, then start ordering.</summary>
    /// <remarks>
    ///     Refuses, before building any wire parameters, a published port whose host interface is not loopback and
    ///     an image reference that is not digest-pinned. Both are checked here rather than only by the policy
    ///     verifier because <c>required</c> means "assigned", not "non-empty": an empty host IP renders as a binding
    ///     Docker resolves to <c>0.0.0.0</c>, and there must be no window in which such a container exists.
    /// </remarks>
    Task<string> RunContainerAsync(ContainerSpecification specification, CancellationToken cancellationToken = default);

    /// <summary>Read back what the daemon says the container actually is — the evidence policy verification runs on.</summary>
    Task<ContainerInspection> InspectAsync(string containerId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stop a container, giving it <paramref name="gracePeriod" /> to exit before it is killed. Returns the
    ///     daemon's own answer: <see langword="false" /> means it was already stopped, which is not an error.
    /// </summary>
    Task<bool> StopContainerAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default);

    /// <summary>Whether the digest-pinned image is already present locally, so an install can skip a pull.</summary>
    Task<bool> ImageExistsAsync(string imageReference, CancellationToken cancellationToken = default);

    /// <summary>Pull a digest-pinned image, reporting aggregated progress. A tag is refused before any wire call.</summary>
    Task PullImageAsync(string imageReference, IProgress<ContainerPullProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Create the instance network and return its id. On a name conflict the existing network is reused only when
    ///     its driver and labels prove it is this instance's own; anything else is refused.
    /// </summary>
    Task<string> CreateNetworkAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken = default);

    /// <summary>Remove a network. A network that is already gone is not an error; one that still has containers attached is.</summary>
    Task RemoveNetworkAsync(string networkNameOrId, CancellationToken cancellationToken = default);

    /// <summary>Ids of every network carrying all of <paramref name="labels" />, filtered daemon-side.</summary>
    Task<IReadOnlyList<string>> ListNetworksAsync(IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken = default);

    /// <summary>Every container carrying all of <paramref name="labels" />, running or not, with the state and exit code the observer and the boot reconciler need.</summary>
    /// <remarks>
    ///     The inherited <see cref="IDockerRuntimeClient.ListContainersAsync" /> is NOT an alternative: it returns
    ///     bare ids, so an exited container is indistinguishable from a running one and a crashed application would
    ///     be reported healthy.
    /// </remarks>
    Task<IReadOnlyList<ContainerSummary>> ListContainersDetailedAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default);

    /// <summary>Read a bounded window of a container's log, with both streams demultiplexed.</summary>
    Task<ContainerLogSnapshot> ReadLogsAsync(string containerId, ContainerLogRequest request, CancellationToken cancellationToken = default);

    /// <summary>Whether the running container can create and delete a file under <paramref name="containerPath" />.</summary>
    /// <remarks>
    ///     The only exec-shaped operation this layer exposes, and it deliberately takes NO command: the caller
    ///     cannot choose what runs. The uid mapping between a container and an engine-created bind mount is a
    ///     premise rather than a construction — under a rootless daemon in-container root maps to the invoking user,
    ///     under a rootful one it does not — so every install proves it and fails naming the daemon mode.
    /// </remarks>
    Task<bool> ProbeWritablePathAsync(string containerId, string containerPath, CancellationToken cancellationToken = default);
}
