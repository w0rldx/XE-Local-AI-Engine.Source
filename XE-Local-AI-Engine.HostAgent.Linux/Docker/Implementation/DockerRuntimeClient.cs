namespace XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;

using System.Formats.Tar;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using global::Docker.DotNet;
using global::Docker.DotNet.Models;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Abstractions.Manifest;

/// <summary>
///     Client boundary for docker runtime operations.
/// </summary>
public sealed class DockerRuntimeClient : IDockerRuntimeClient, IDisposable
{
    private const string RunningState = "running";
    private const long BytesPerMegabyte = 1024L * 1024L;

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

    public async Task<string> CreateSandboxContainerAsync(SandboxContainerSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Image);

        var created = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = spec.Name,
                Image = spec.Image,
                Cmd = ["sleep", "infinity"],
                User = spec.User,
                Labels = spec.Labels.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                Env = spec.Environment.Select(static pair => $"{pair.Key}={pair.Value}").ToList(),
                HostConfig = BuildSandboxHostConfig(spec)
            },
            cancellationToken).ConfigureAwait(false);

        await _client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken)
                     .ConfigureAwait(false);

        return created.ID;
    }

    /// <summary>
    ///     Builds the hardened <see cref="HostConfig" /> for a sandbox container: a locked-down
    ///     no-network default, no-new-privileges, all capabilities dropped, plus the spec's resource ceilings. Exposed
    ///     so the resource/network mapping can be asserted without a Docker daemon. NEVER mounts the Docker socket and
    ///     never adds writable host binds.
    /// </summary>
    public static HostConfig BuildSandboxHostConfig(SandboxContainerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // D6: a tag-only local image is accepted as-is by the caller; it is NOT routed through the strict
        // managed-runtime DockerImageReference.Parse (which mandates repo:tag@sha256 and rejects :latest).
        var hostConfig = new HostConfig
        {
            // MVP: both None and the (reserved) Restricted posture map to the no-network default; a real restricted
            // egress policy is post-MVP. None is the secure default.
            NetworkMode = "none",
            SecurityOpt = ["no-new-privileges"],
            CapDrop = ["ALL"],
            ReadonlyRootfs = false,
            AutoRemove = false
        };

        if (spec.MemoryMb is { } memoryMb && memoryMb > 0)
        {
            hostConfig.Memory = memoryMb * BytesPerMegabyte;
        }

        if (spec.CpuCount is { } cpuCount && cpuCount > 0)
        {
            hostConfig.NanoCPUs = (long)(cpuCount * 1_000_000_000d);
        }

        if (spec.PidsLimit is { } pidsLimit && pidsLimit > 0)
        {
            hostConfig.PidsLimit = pidsLimit;
        }

        return hostConfig;
    }

    public async Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken)
                                     .ConfigureAwait(false);

        var match = containers.FirstOrDefault(container =>
            container.Names is not null
            && container.Names.Any(name => string.Equals(NormalizeContainerName(name), containerName, StringComparison.Ordinal)));

        return match?.ID;
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetSandboxContainerLabelsAsync(string containerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            var inspect = await _client.Containers.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            var labels = inspect.Config?.Labels;
            return labels is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(labels, StringComparer.Ordinal);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    public async Task<DockerExecResult> ExecInContainerAsync(string containerId, DockerExecRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = TimeProvider.System.GetTimestamp();

        // A timeout-linked CTS combined with the caller's token; cancelling either tears down the read-loop (D9).
        using var timeoutCts = request.Timeout is { } timeout && timeout > TimeSpan.Zero
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var execToken = linkedCts.Token;

        var argv = new List<string>(request.Arguments.Count + 1) { request.Executable };
        argv.AddRange(request.Arguments);

        var execCreate = await _client.Exec.CreateContainerExecAsync(containerId, new ContainerExecCreateParameters
            {
                AttachStdin = !request.StandardInput.IsEmpty,
                AttachStdout = true,
                AttachStderr = true,
                Cmd = argv,
                Env = request.Environment.Select(static pair => $"{pair.Key}={pair.Value}").ToList(),
                WorkingDir = request.WorkingDirectory ?? string.Empty,
                TTY = false
            },
            execToken).ConfigureAwait(false);

        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var completed = true;

        try
        {
            using var stream = await _client.Exec.StartContainerExecAsync(execCreate.ID, new ContainerExecStartParameters
                {
                    Detach = false,
                    TTY = false
                },
                execToken).ConfigureAwait(false);

            if (!request.StandardInput.IsEmpty)
            {
                var stdin = request.StandardInput.ToArray();
                await stream.WriteAsync(stdin, 0, stdin.Length, execToken).ConfigureAwait(false);
                stream.CloseWrite();
            }

            await stream.CopyOutputToAsync(Stream.Null, stdout, stderr, execToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort cancel/timeout (D9): the read-loop was torn down; report a non-completed result rather than
            // rethrowing so the worker maps it onto SandboxCommandResult { Completed = false, ExitCode = -1 }.
            completed = false;
        }

        var duration = TimeProvider.System.GetElapsedTime(startedAt);

        if (!completed)
        {
            return new DockerExecResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = -1,
                StandardOutput = DecodeUtf8(stdout),
                StandardError = DecodeUtf8(stderr),
                Completed = false,
                Duration = duration
            };
        }

        var inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID, cancellationToken).ConfigureAwait(false);

        return new DockerExecResult
        {
            ExecutionId = request.ExecutionId,
            ExitCode = (int)(inspect.ExitCode ?? -1),
            StandardOutput = DecodeUtf8(stdout),
            StandardError = DecodeUtf8(stderr),
            Completed = true,
            Duration = duration
        };
    }

    public async Task CopyIntoContainerAsync(string containerId,
        string destinationPath,
        ReadOnlyMemory<byte> content,
        int fileMode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var (directory, fileName) = SplitContainerPath(destinationPath);

        // ExtractArchiveToContainerAsync wants the destination DIRECTORY plus a tar holding the single file entry.
        await using var tar = new MemoryStream();
        using (var data = new MemoryStream(content.ToArray()))
        await using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                // UnixFileMode is a POSIX-octal flags enum; the normalized 9-bit mode maps directly.
                Mode = (UnixFileMode)NormalizeFileMode(fileMode),
                DataStream = data
            };
            await writer.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        tar.Position = 0;
        await _client.Containers.ExtractArchiveToContainerAsync(containerId,
            new CopyToContainerParameters { Path = directory },
            tar,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var archive = await _client.Containers.GetArchiveFromContainerAsync(containerId,
            new ContainerPathStatParameters { Path = sourcePath },
            statOnly: false,
            cancellationToken).ConfigureAwait(false);

        if (archive.Stream is null)
        {
            throw new FileNotFoundException($"The container returned no archive stream for '{sourcePath}'.", sourcePath);
        }

        await using var tarStream = archive.Stream;
        await using var reader = new TarReader(tarStream, leaveOpen: true);

        var entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        while (entry is not null)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.DataStream is not null)
            {
                using var buffer = new MemoryStream();
                await entry.DataStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                return buffer.ToArray();
            }

            entry = await reader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        throw new FileNotFoundException($"No file entry was found in the archive for '{sourcePath}'.", sourcePath);
    }

    public Task RemoveSandboxContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        return _client.Containers.RemoveContainerAsync(containerId,
            new ContainerRemoveParameters { Force = true },
            cancellationToken);
    }

    private static string DecodeUtf8(MemoryStream stream)
    {
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static int NormalizeFileMode(int fileMode)
    {
        // Default to rw-r--r-- when no usable mode was supplied; never honor an executable/world-writable surprise.
        return fileMode is > 0 and <= 0x1FF ? fileMode : 0b110_100_100;
    }

    private static (string Directory, string FileName) SplitContainerPath(string destinationPath)
    {
        var normalized = destinationPath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return ("/", lastSlash == 0 ? normalized[1..] : normalized);
        }

        var directory = normalized[..lastSlash];
        var fileName = normalized[(lastSlash + 1)..];
        return (directory, fileName);
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
