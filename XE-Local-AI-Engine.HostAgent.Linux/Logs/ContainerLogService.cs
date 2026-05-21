namespace XE_Local_AI_Engine.HostAgent.Linux.Logs;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;

public sealed class ContainerLogService
{
    private readonly IDockerRuntimeClient _dockerRuntimeClient;

    public ContainerLogService(IDockerRuntimeClient dockerRuntimeClient)
    {
        _dockerRuntimeClient = dockerRuntimeClient;
    }

    public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        await foreach (var line in _dockerRuntimeClient.StreamLogsAsync(containerName,
                           Math.Max(0, tailLines),
                           follow,
                           cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }
    }
}
