namespace XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

public sealed class FakeDockerRuntimeClient : IDockerRuntimeClient
{
    private const string RuntimeNetwork = "xe-engine-net";
    private readonly ConcurrentDictionary<string, DockerContainerStatus> _containers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public FakeDockerRuntimeClient(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        SeedContainer("ollama", "ollama/ollama:dev@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        SeedContainer("xe-node-web-server", "ghcr.io/c0re/xe-local-ai-engine:dev@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    }

    public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        cancellationToken.ThrowIfCancellationRequested();

        _containers.TryAdd(container.Name, CreateContainer(container.Name, container.Image, false));
        return Task.CompletedTask;
    }

    public Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<string?>(image.Digest);
    }

    public Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DockerContainerStatus>>(_containers.Values.OrderBy(static container => container.Name, StringComparer.Ordinal).ToArray());
    }

    public Task StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        UpdateContainer(containerName, true, cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        UpdateContainer(containerName, false, cancellationToken);
        return Task.CompletedTask;
    }

    public Task RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        UpdateContainer(containerName, true, cancellationToken);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();

        var count = Math.Max(1, tailLines == 0 ? 1 : tailLines);
        for (var index = 0; index < count; index++)
        {
            yield return CreateLogLine(containerName, $"fake docker log {index + 1}");
        }

        while (follow && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            yield return CreateLogLine(containerName, "fake docker heartbeat");
        }
    }

    private void SeedContainer(string name, string imageReference)
    {
        _containers[name] = CreateContainer(name, imageReference, true);
    }

    private void UpdateContainer(string containerName, bool isRunning, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();

        _containers.AddOrUpdate(containerName,
            name => CreateContainer(name, $"local/{name}:dev@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", isRunning),
            (_, existing) => existing with
            {
                State = isRunning ? "running" : "exited",
                IsRunning = isRunning
            });
    }

    private static DockerContainerStatus CreateContainer(string name, string imageReference, bool isRunning)
    {
        return new DockerContainerStatus
        {
            Name = name,
            ImageReference = imageReference,
            State = isRunning ? "running" : "exited",
            IsRunning = isRunning,
            NetworkNames = [RuntimeNetwork]
        };
    }

    private DockerLogLine CreateLogLine(string containerName, string line)
    {
        return new DockerLogLine
        {
            ContainerName = containerName,
            Stream = "stdout",
            Line = line,
            ObservedAt = _timeProvider.GetUtcNow()
        };
    }
}
