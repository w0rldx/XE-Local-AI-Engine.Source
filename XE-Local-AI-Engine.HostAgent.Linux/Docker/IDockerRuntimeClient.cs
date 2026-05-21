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
}
