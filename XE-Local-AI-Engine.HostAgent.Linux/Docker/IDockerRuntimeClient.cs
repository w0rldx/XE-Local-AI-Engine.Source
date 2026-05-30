namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

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

    // --- AgentHome sandbox operations (Marker J-local, plan §4.1) ---

    /// <summary>Creates and starts a dedicated, hardened sandbox container (D5) and returns its container id.</summary>
    Task<string> CreateSandboxContainerAsync(SandboxContainerSpec spec, CancellationToken cancellationToken);

    /// <summary>Finds an existing sandbox container by name, returning its container id or <see langword="null" />.</summary>
    Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads the Docker labels stamped on a sandbox container, or <see langword="null" /> when the container no
    ///     longer exists. Used to validate the attach key (owner/node/profile/manifest) against the labels the
    ///     container was created with (plan §6.2.1 rule 15).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>?> GetSandboxContainerLabelsAsync(string containerId, CancellationToken cancellationToken);

    /// <summary>Execs a command inside the container, capturing stdout/stderr and the exit code (D9 best-effort cancel).</summary>
    Task<DockerExecResult> ExecInContainerAsync(string containerId, DockerExecRequest request, CancellationToken cancellationToken);

    /// <summary>Writes <paramref name="content" /> to <paramref name="destinationPath" /> inside the container (whole-file, D4).</summary>
    Task CopyIntoContainerAsync(string containerId, string destinationPath, ReadOnlyMemory<byte> content, int fileMode, CancellationToken cancellationToken);

    /// <summary>Reads the file at <paramref name="sourcePath" /> out of the container as raw bytes.</summary>
    Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken);

    /// <summary>Force-removes a sandbox container (kill + rm); subsequent operations against it fail.</summary>
    Task RemoveSandboxContainerAsync(string containerId, CancellationToken cancellationToken);
}
