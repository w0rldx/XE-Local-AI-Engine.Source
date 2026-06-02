namespace XE_Local_AI_Engine.HostAgent.Linux.Logs;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;

public sealed class ContainerLogService
{
    private readonly IDockerRuntimeClient _dockerRuntimeClient;
    private readonly HostAgentRuntimeOptions _runtimeOptions;

    public ContainerLogService(IDockerRuntimeClient dockerRuntimeClient)
        : this(dockerRuntimeClient, new HostAgentRuntimeOptions())
    {
    }

    public ContainerLogService(IDockerRuntimeClient dockerRuntimeClient, HostAgentRuntimeOptions runtimeOptions)
    {
        _dockerRuntimeClient = dockerRuntimeClient;
        _runtimeOptions = runtimeOptions;
    }

    public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Fail-closed: a name the node does not own (or any name when no manifest scopes ownership) is never
        // streamed to Docker, mirroring the listing/action ownership boundary. An unowned request yields nothing.
        if (!ContainerOwnership.Owns(_runtimeOptions.Manifest, containerName))
        {
            yield break;
        }

        await foreach (var line in _dockerRuntimeClient.StreamLogsAsync(containerName,
                           Math.Max(0, tailLines),
                           follow,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }
}
