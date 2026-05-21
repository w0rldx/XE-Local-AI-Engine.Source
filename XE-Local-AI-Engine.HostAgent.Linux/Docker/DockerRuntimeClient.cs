namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using global::Docker.DotNet;
using global::Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

public sealed class DockerRuntimeClient : IDockerRuntimeClient, IDisposable
{
    private const string RunningState = "running";
    private readonly DockerClient _client;

    public DockerRuntimeClient(IOptions<HostAgentDockerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = new DockerClientBuilder().WithEndpoint(new Uri(options.Value.Endpoint)).Build();
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    public async Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);

        var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken).ConfigureAwait(false);
        if (networks.Any(network => string.Equals(network.Name, networkName, StringComparison.Ordinal)))
        {
            return;
        }

        await _client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = networkName,
                Driver = "bridge"
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureContainerAsync(ContainerManifest container, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);

        if (await FindContainerAsync(container.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var image = DockerImageReference.Parse(container.Image);
        await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = container.Name,
                Image = image.RepositoryWithTag,
                Env = container.Environment.Select(static pair => $"{pair.Key}={pair.Value}").ToList(),
                HostConfig = new HostConfig
                {
                    Binds = container.Volumes.Select(ToBindMount).ToList(),
                    NetworkMode = container.Network
                },
                NetworkingConfig = new NetworkingConfig
                {
                    EndpointsConfig = new Dictionary<string, EndpointSettings>
                    {
                        [container.Network] = new()
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PullImageAsync(DockerImageReference image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        await _client.Images.CreateImageAsync(new ImagesCreateParameters
            {
                FromImage = image.Repository,
                Tag = image.Tag
            },
            null,
            new Progress<JSONMessage>(),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> InspectImageDigestAsync(DockerImageReference image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);

        var response = await _client.Images.InspectImageAsync(image.RepositoryWithTag, cancellationToken).ConfigureAwait(false);
        var repoDigest = response.RepoDigests?.FirstOrDefault(digest =>
            digest.StartsWith($"{image.Repository}@sha256:", StringComparison.Ordinal));

        return repoDigest is null ? null : repoDigest[(repoDigest.LastIndexOf(':') + 1)..];
    }

    public async Task<IReadOnlyList<DockerContainerStatus>> ListContainersAsync(CancellationToken cancellationToken)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true
            },
            cancellationToken).ConfigureAwait(false);

        return containers.Select(container => new DockerContainerStatus
                         {
                             Name = NormalizeContainerName(container.Names.FirstOrDefault() ?? container.ID),
                             ImageReference = container.Image,
                             NetworkNames = container.NetworkSettings?.Networks?.Keys.ToArray() ?? [],
                             State = container.State ?? string.Empty,
                             IsRunning = string.Equals(container.State, RunningState, StringComparison.OrdinalIgnoreCase)
                         })
                         .ToArray();
    }

    public async Task StartContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var container = await FindContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
        if (container is null || container.IsRunning)
        {
            return;
        }

        await _client.Containers.StartContainerAsync(containerName, new ContainerStartParameters(), cancellationToken)
                     .ConfigureAwait(false);
    }

    public async Task StopContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        var container = await FindContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
        if (container is null || !container.IsRunning)
        {
            return;
        }

        await _client.Containers.StopContainerAsync(containerName,
            new ContainerStopParameters
            {
                WaitBeforeKillSeconds = (uint)Math.Ceiling(drainTimeout.TotalSeconds)
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartContainerAsync(string containerName, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        var container = await FindContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
        if (container is null)
        {
            return;
        }

        if (!container.IsRunning)
        {
            await StartContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _client.Containers.RestartContainerAsync(containerName,
            new ContainerRestartParameters
            {
                WaitBeforeKillSeconds = (uint)Math.Ceiling(drainTimeout.TotalSeconds)
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<DockerLogLine> StreamLogsAsync(string containerName,
        int tailLines,
        bool follow,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        using var stream = await _client.Containers.GetContainerLogsAsync(containerName,
            new ContainerLogsParameters
            {
                Follow = follow,
                ShowStdout = true,
                ShowStderr = true,
                Tail = Math.Max(0, tailLines).ToString(CultureInfo.InvariantCulture)
            },
            cancellationToken).ConfigureAwait(false);

        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read.EOF)
            {
                yield break;
            }

            if (read.Count <= 0)
            {
                continue;
            }

            foreach (var line in SplitLines(Encoding.UTF8.GetString(buffer, 0, read.Count)))
            {
                yield return new DockerLogLine
                {
                    ContainerName = containerName,
                    Stream = read.Target.ToString(),
                    Line = line,
                    ObservedAt = DateTimeOffset.UtcNow
                };
            }
        }
    }

    private async Task<DockerContainerStatus?> FindContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        var containers = await ListContainersAsync(cancellationToken).ConfigureAwait(false);
        return containers.FirstOrDefault(container => string.Equals(container.Name, containerName, StringComparison.Ordinal));
    }

    private static string NormalizeContainerName(string name)
    {
        return name.Length > 0 && name[0] == '/' ? name[1..] : name;
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string ToBindMount(VolumeMountManifest volume)
    {
        var mode = volume.ReadOnly ? "ro" : "rw";
        return $"{volume.Source}:{volume.Target}:{mode}";
    }
}
