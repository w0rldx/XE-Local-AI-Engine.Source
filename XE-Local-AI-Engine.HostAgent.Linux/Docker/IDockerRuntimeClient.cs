namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

/// <summary>
///     Client boundary for i docker runtime operations.
/// </summary>
public interface IDockerRuntimeClient
{
    Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken);

    Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken);

    Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken);

    Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken);

    Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken);

    Task StartContainerAsync(string containerName, CancellationToken cancellationToken);

    Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken);

    Task RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken);

    IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        CancellationToken cancellationToken);

    // --- AgentHome sandbox operations ---

    /// <summary>Creates and starts a dedicated, hardened sandbox container and returns its container id.</summary>
    Task<string> CreateSandboxContainerAsync(SandboxContainerSpec spec, CancellationToken cancellationToken);

    /// <summary>Finds an existing sandbox container by name, returning its container id or <see langword="null" />.</summary>
    Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads the Docker labels stamped on a sandbox container, or <see langword="null" /> when the container no
    ///     longer exists. Used to validate the attach key (owner/node/profile/manifest) against the labels the
    ///     container was created with.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetSandboxContainerLabelsAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Execs a command inside the container, capturing stdout/stderr and the exit code (best-effort cancel).</summary>
    Task<DockerExecResult> ExecInContainerAsync(string containerId, DockerExecRequest request, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="content" /> to <paramref name="destinationPath" /> inside the container as a whole-file byte payload.</summary>
    Task CopyIntoContainerAsync(string containerId, string destinationPath, ReadOnlyMemory<byte> content, int fileMode, CancellationToken cancellationToken);

    /// <summary>Reads the file at <paramref name="sourcePath" /> out of the container as raw bytes.</summary>
    Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken);

    /// <summary>Force-removes a sandbox container (kill + rm); subsequent operations against it fail.</summary>
    Task RemoveSandboxContainerAsync(string containerId, CancellationToken cancellationToken);

    // --- Model-fit utility one-shot run (narrow approved-image llmfit runner, plan Marker 2) ---

    /// <summary>
    ///     Runs a single one-shot utility container to completion and returns its captured result (plan Marker 2). The
    ///     container is created from the pinned <see cref="UtilityContainerRunSpec.Image" /> with the supplied argv, the
    ///     least-privilege hardening posture, and the requested network, then awaited for exit under a timeout. The
    ///     container is removed afterwards (force) UNLESS the run failed and
    ///     <see cref="UtilityContainerRunSpec.RetainOnFailure" /> is set. On cancellation/timeout the container is
    ///     stopped/killed and removed (subject to the same retention rule) and a non-completed result is returned. NEVER
    ///     mounts the Docker socket or host binds.
    /// </summary>
    Task<UtilityContainerRunResult> RunUtilityContainerAsync(UtilityContainerRunSpec spec, CancellationToken cancellationToken);

    /// <summary>
    ///     Best-effort removal of leftover model-fit utility containers (those stamped with the utility label) from a
    ///     prior crash. Returns the count removed. Used by the startup reconciler so orphaned llmfit containers do not
    ///     keep consuming resources.
    /// </summary>
    Task<int> RemoveOrphanedUtilityContainersAsync(CancellationToken cancellationToken);
}
