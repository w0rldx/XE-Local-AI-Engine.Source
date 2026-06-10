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

    // --- Model-fit utility one-shot run (narrow approved-image llmfit runner) ---

    /// <summary>The Docker label stamped on every utility container so orphans can be reconciled on startup.</summary>
    public const string UtilityLabelKey = "xe.modelfit.utility";

    private const string UtilityLabelValue = "1";

    private readonly IDockerClient _client;

    public DockerRuntimeClient(IOptions<HostAgentDockerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = new DockerClientBuilder().WithEndpoint(new Uri(options.Value.Endpoint)).Build();
    }

    /// <summary>Test seam: injects a pre-built <see cref="IDockerClient" /> directly (e.g. a substitute).</summary>
    internal DockerRuntimeClient(IDockerClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
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

        if (repoDigest is not null)
        {
            // Pulled image: return the registry RepoDigest hex (repo@sha256:<hex> → strip up to last ':').
            return repoDigest[(repoDigest.LastIndexOf(':') + 1)..];
        }

        // Locally loaded image (docker load of a docker save tar): RepoDigests is empty because no registry
        // push/pull has occurred.  Fall back to the image config Id, which is stable across save/load and is
        // the identity scheme used for bundled managed images (§7.6).  The config Id has the same
        // "sha256:<hex>" shape as a RepoDigest entry, so the same LastIndexOf slice applies.
        if (response.ID is not null)
        {
            return response.ID[(response.ID.LastIndexOf(':') + 1)..];
        }

        return null;
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

    public async Task<string?> FindSandboxContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);

        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
                                      {
                                          All = true
                                      }, cancellationToken)
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

        var argv = new List<string>(request.Arguments.Count + 1)
        {
            request.Executable
        };
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
        {
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
        }

        tar.Position = 0;
        await _client.Containers.ExtractArchiveToContainerAsync(containerId,
            new CopyToContainerParameters
            {
                Path = directory
            },
            tar,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadFromContainerAsync(string containerId, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var archive = await _client.Containers.GetArchiveFromContainerAsync(containerId,
            new ContainerPathStatParameters
            {
                Path = sourcePath
            },
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
            new ContainerRemoveParameters
            {
                Force = true
            },
            cancellationToken);
    }

    public async Task<UtilityContainerRunResult> RunUtilityContainerAsync(UtilityContainerRunSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Image);

        var startedAt = TimeProvider.System.GetTimestamp();

        // The image is digest-pinned; pull it if it is not already present (mirrors PullImageAsync).
        var image = DockerImageReference.Parse(spec.Image);
        await PullImageAsync(image, cancellationToken).ConfigureAwait(false);

        var networkName = string.IsNullOrWhiteSpace(spec.NetworkName) ? null : spec.NetworkName;

        var createParameters = new CreateContainerParameters
        {
            // Run by canonical digest form so the started container is byte-identical to the validated reference.
            Image = image.CanonicalReference,
            Cmd = spec.Arguments.ToList(),
            User = spec.User,
            Env = spec.Environment.Select(static pair => $"{pair.Key}={pair.Value}").ToList(),
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [UtilityLabelKey] = UtilityLabelValue
            },
            HostConfig = BuildUtilityHostConfig(spec, networkName)
        };

        if (networkName is not null)
        {
            // Attach to the managed runtime network so the provider DNS name (e.g. http://ollama:11434) resolves —
            // mirrors EnsureContainerAsync's NetworkingConfig endpoint registration.
            createParameters.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkName] = new()
                }
            };
        }

        var created = await _client.Containers.CreateContainerAsync(createParameters, cancellationToken).ConfigureAwait(false);
        var containerId = created.ID;

        // A timeout-linked CTS combined with the caller's token; cancelling either tears down the wait (mirrors
        // ExecInContainerAsync's D9 pattern). No explicit timeout means wait until exit or caller cancel.
        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var runToken = linkedCts.Token;

        var completed = true;
        var exitCode = -1;
        var removed = false;

        try
        {
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), runToken).ConfigureAwait(false);

            var wait = await _client.Containers.WaitContainerAsync(containerId, runToken).ConfigureAwait(false);
            exitCode = (int)wait.StatusCode;

            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            using (var logStream = await _client.Containers.GetContainerLogsAsync(containerId,
                       new ContainerLogsParameters
                       {
                           Follow = false,
                           ShowStdout = true,
                           ShowStderr = true
                       },
                       runToken).ConfigureAwait(false))
            {
                await logStream.CopyOutputToAsync(Stream.Null, stdout, stderr, runToken).ConfigureAwait(false);
            }

            var duration = TimeProvider.System.GetElapsedTime(startedAt);
            var standardOutput = DecodeUtf8(stdout);
            var standardError = DecodeUtf8(stderr);

            // Remove the container on the normal path unless it failed and debug retention asked us to keep it.
            if (!(exitCode != 0 && spec.RetainOnFailure))
            {
                await RemoveUtilityContainerQuietlyAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            }

            removed = true;

            return new UtilityContainerRunResult
            {
                ExitCode = exitCode,
                StandardOutput = standardOutput,
                StandardError = standardError,
                Completed = true,
                Duration = duration
            };
        }
        catch (OperationCanceledException)
        {
            // Cancellation or timeout: stop/kill and remove the container (unless debug retention is enabled), then
            // report a non-completed result the service maps onto CANCELLED/TIMED_OUT.
            completed = false;
            await StopUtilityContainerQuietlyAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            if (!spec.RetainOnFailure)
            {
                await RemoveUtilityContainerQuietlyAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            }

            removed = true;

            return new UtilityContainerRunResult
            {
                ExitCode = -1,
                Completed = false,
                Duration = TimeProvider.System.GetElapsedTime(startedAt)
            };
        }
        finally
        {
            // Defense in depth: if an unexpected exception escaped before either path removed the container, force-remove
            // it so a created-but-orphaned container never lingers (retention only applies to a known-failed run).
            if (!removed && !(!completed && spec.RetainOnFailure))
            {
                await RemoveUtilityContainerQuietlyAsync(containerId, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<int> RemoveOrphanedUtilityContainersAsync(CancellationToken cancellationToken)
    {
        var containers = await _client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool>
                    {
                        [$"{UtilityLabelKey}={UtilityLabelValue}"] = true
                    }
                }
            },
            cancellationToken).ConfigureAwait(false);

        var removed = 0;
        foreach (var containerId in containers.Select(static container => container.ID))
        {
            await StopUtilityContainerQuietlyAsync(containerId, cancellationToken).ConfigureAwait(false);
            if (await RemoveUtilityContainerQuietlyAsync(containerId, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        return removed;
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
            // Both None and the reserved Restricted posture map to the no-network default; a restricted egress policy
            // is not implemented yet. None is the secure default.
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

    /// <summary>
    ///     Builds the hardened <see cref="HostConfig" /> for a utility container: the same least-privilege posture as
    ///     <see cref="BuildSandboxHostConfig" /> (no-new-privileges, all capabilities dropped, no socket, no binds,
    ///     AutoRemove off) plus the run's resource ceilings. The only difference is the network: <c>"none"</c> when no
    ///     runtime network is requested, otherwise the named managed runtime network. Exposed so the mapping can be
    ///     asserted without a Docker daemon.
    /// </summary>
    public static HostConfig BuildUtilityHostConfig(UtilityContainerRunSpec spec, string? networkName)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var hostConfig = new HostConfig
        {
            NetworkMode = string.IsNullOrWhiteSpace(networkName) ? "none" : networkName,
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

    private async Task StopUtilityContainerQuietlyAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.Containers.StopContainerAsync(containerId,
                new ContainerStopParameters
                {
                    WaitBeforeKillSeconds = 1
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException)
        {
            // Already stopped / gone — removal below is the authoritative cleanup.
        }
    }

    private async Task<bool> RemoveUtilityContainerQuietlyAsync(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.Containers.RemoveContainerAsync(containerId,
                new ContainerRemoveParameters
                {
                    Force = true
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
        catch (DockerApiException)
        {
            // Best-effort cleanup: a removal failure must not mask the run result. The startup reconciler retries.
            return false;
        }
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
