namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;

/// <summary>
///     The production <see cref="IDockerRuntimeClient" />: a thin adapter over <c>Docker.DotNet.Enhanced</c>.
///     <para>
///         Thin on purpose. Every decision — what a hardened container is, whether the settings took, which daemon is
///         approved — lives above this class in code that a fake client can drive. What lives here is the wire
///         translation and, importantly, the classification of transport failures into the outcomes an operator can
///         act on: a missing socket, a socket that refuses, and a daemon that answered are three different problems
///         with three different fixes, and the daemon does not label them for us.
///     </para>
/// </summary>
internal sealed class DockerDotNetRuntimeClient : IDockerRuntimeClient
{
    private readonly DockerClient _client;
    private readonly TimeSpan _probeTimeout;

    public DockerDotNetRuntimeClient(DockerDaemonEndpoint endpoint, TimeSpan probeTimeout)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _probeTimeout = probeTimeout;
        _client = new DockerClientBuilder().WithEndpoint(endpoint.Uri).WithTimeout(probeTimeout).Build();
    }

    public DockerDaemonEndpoint Endpoint { get; }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task<DockerDaemonIdentity> ProbeAsync(CancellationToken cancellationToken = default)
    {
        // A Unix socket that is simply not there surfaces from the socket layer as AddressNotAvailable, which reads to
        // an operator as a networking fault rather than as "Docker is not running". Checking the path first turns the
        // most common failure on this platform into the message that names its own fix.
        var socketPath = Endpoint.UnixSocketPath;
        if (socketPath is not null && !File.Exists(socketPath) && !Directory.Exists(socketPath))
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"No Docker socket exists at '{socketPath}'.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_probeTimeout);

            await _client.System.PingAsync(timeout.Token).ConfigureAwait(false);
            var version = await _client.System.GetVersionAsync(timeout.Token).ConfigureAwait(false);
            var info = await _client.System.GetSystemInfoAsync(timeout.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(info.ID))
            {
                throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                    "The Docker daemon did not report an installation id, so this node cannot pin which daemon it is talking to.");
            }

            return new DockerDaemonIdentity(info.ID,
                version.Version ?? string.Empty,
                version.APIVersion ?? string.Empty,
                version.MinAPIVersion ?? string.Empty,
                version.Os ?? string.Empty,
                Endpoint);
        }
        catch (Exception exception) when (exception is not DockerRuntimeException and not OperationCanceledException)
        {
            throw Classify(exception);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"The Docker daemon at '{Endpoint.Display}' did not answer within {_probeTimeout.TotalSeconds:0} seconds.");
        }
    }

    public async Task<string> CreateContainerAsync(DockerContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var parameters = new CreateContainerParameters
        {
            Image = specification.Image,
            Name = specification.Name,
            User = specification.User,
            WorkingDir = specification.WorkingDirectory,
            Entrypoint = [.. specification.Entrypoint],
            Cmd = [.. specification.Command],
            Labels = specification.Labels.ToDictionary(StringComparer.Ordinal),
            HostConfig = new HostConfig
            {
                NetworkMode = specification.NetworkMode,
                Privileged = false,
                CapDrop = [.. specification.CapabilitiesToDrop],
                CapAdd = [],
                SecurityOpt = [.. specification.SecurityOptions],
                ReadonlyRootfs = specification.ReadOnlyRootFilesystem,
                Tmpfs = specification.TemporaryFilesystems.ToDictionary(StringComparer.Ordinal),
                Mounts = [.. specification.BindMounts.Select(ToMount)],
                Memory = specification.MemoryBytes,
                NanoCPUs = specification.NanoCpus,
                PidsLimit = specification.PidsLimit,
                Devices = [],
                DeviceRequests = [],
                // Left at the daemon's private defaults rather than set to "private": Docker rejects an explicit
                // "private" for PidMode, and the verifier treats an empty mode as private, which is what it is.
                IpcMode = "private",
                AutoRemove = false
            }
        };

        try
        {
            var created = await _client.Containers.CreateContainerAsync(parameters, cancellationToken).ConfigureAwait(false);
            return created.ID;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await _client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken)
                         .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<DockerContainerSettings> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            var inspected = await _client.Containers.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            var hostConfig = inspected.HostConfig
                             ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                                 $"The Docker daemon returned no host configuration for container '{containerId}', so its isolation settings cannot be verified.");

            return new DockerContainerSettings
            {
                ContainerId = inspected.ID,
                User = inspected.Config?.User ?? string.Empty,
                NetworkMode = hostConfig.NetworkMode ?? string.Empty,
                Privileged = hostConfig.Privileged,
                ReadOnlyRootFilesystem = hostConfig.ReadonlyRootfs,
                CapabilitiesDropped = hostConfig.CapDrop?.ToArray() ?? [],
                CapabilitiesAdded = hostConfig.CapAdd?.ToArray() ?? [],
                SecurityOptions = hostConfig.SecurityOpt?.ToArray() ?? [],
                TemporaryFilesystems = hostConfig.Tmpfs is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(hostConfig.Tmpfs, StringComparer.Ordinal),
                Mounts = hostConfig.Mounts?.Select(FromMount).ToArray() ?? [],
                MemoryBytes = hostConfig.Memory,
                NanoCpus = hostConfig.NanoCPUs,
                PidsLimit = hostConfig.PidsLimit ?? 0,
                DeviceCount = (hostConfig.Devices?.Count ?? 0) + (hostConfig.DeviceRequests?.Count ?? 0),
                PidMode = hostConfig.PidMode ?? string.Empty,
                IpcMode = hostConfig.IpcMode ?? string.Empty,
                UtsMode = hostConfig.UTSMode ?? string.Empty
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            await _client.Containers
                         .RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true, RemoveVolumes = true }, cancellationToken)
                         .ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone. Removal is idempotent by contract so that the fail-closed create path can always clean up
            // after itself without having to reason about how far it got.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<DockerExecutionOutcome> ExecuteAsync(string containerId,
        DockerExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var parameters = new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                AttachStdin = false,
                TTY = false,
                Cmd = [request.Executable, .. request.Arguments],
                WorkingDir = request.WorkingDirectory ?? string.Empty,
                Env = request.Environment?.Select(pair => pair.Key + "=" + pair.Value).ToArray() ?? []
            };

            var created = await _client.Exec.CreateContainerExecAsync(containerId, parameters, cancellationToken).ConfigureAwait(false);
            using var stream = await _client.Exec
                                            .StartContainerExecAsync(created.ID, new ContainerExecStartParameters { Detach = false, TTY = false }, cancellationToken)
                                            .ConfigureAwait(false);

            var (standardOutput, standardError) = await stream.ReadOutputToEndAsync(cancellationToken).ConfigureAwait(false);
            var inspected = await _client.Exec.InspectContainerExecAsync(created.ID, cancellationToken).ConfigureAwait(false);

            var (outputText, outputTruncated) = Truncate(standardOutput, request.MaxCapturedBytes);
            var (errorText, errorTruncated) = Truncate(standardError, request.MaxCapturedBytes);

            return new DockerExecutionOutcome
            {
                ExitCode = inspected.ExitCode ?? -1,
                StandardOutput = outputText,
                StandardError = errorText,
                StandardOutputTruncated = outputTruncated,
                StandardErrorTruncated = errorTruncated
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    private static Mount ToMount(DockerBindMount bindMount)
    {
        return new Mount
        {
            Type = "bind",
            Source = bindMount.HostPath,
            Target = bindMount.ContainerPath,
            ReadOnly = bindMount.ReadOnly,
            BindOptions = new BindOptions { Propagation = bindMount.Propagation }
        };
    }

    private static DockerBindMount FromMount(Mount mount)
    {
        return new DockerBindMount
        {
            HostPath = mount.Source ?? string.Empty,
            ContainerPath = mount.Target ?? string.Empty,
            // A read-write mount comes back with ReadOnly absent rather than false (measured against Engine 29.6.1),
            // so a null must read as "writable" — reading it as "unknown" would fail the read-only check on every
            // ordinary workspace mount.
            ReadOnly = mount.ReadOnly ?? false,
            Propagation = mount.BindOptions?.Propagation ?? string.Empty
        };
    }

    private static (string Text, bool Truncated) Truncate(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return (value, false);
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        // Decode with a replacement fallback so a cut through a multi-byte sequence yields a replacement character
        // rather than throwing — truncation is a bounded-capture policy, not a data-integrity failure.
        return (Encoding.UTF8.GetString(bytes, 0, maxBytes), true);
    }

    /// <summary>
    ///     Classify a transport or API failure into the operator-actionable outcome behind it. The socket error code
    ///     is the load-bearing signal: <c>AccessDenied</c> means the socket is there and this process may not use it,
    ///     which is a permissions fix, whereas every other connect failure means nothing is listening, which is a
    ///     "start the daemon" fix. Matching on daemon prose instead would break on the next Docker release.
    /// </summary>
    private DockerRuntimeException Classify(Exception exception)
    {
        var socketException = FindSocketException(exception);
        if (socketException is not null)
        {
            return socketException.SocketErrorCode == SocketError.AccessDenied
                ? new DockerRuntimeException(DockerDaemonPreflightStatus.PermissionDenied,
                    $"Access to the Docker endpoint '{Endpoint.Display}' was denied.", exception)
                : new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                    $"The Docker endpoint '{Endpoint.Display}' could not be reached ({socketException.SocketErrorCode}).", exception);
        }

        if (exception is DockerApiException apiException)
        {
            return new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                $"The Docker daemon at '{Endpoint.Display}' rejected the request with {apiException.StatusCode}.", exception);
        }

        if (exception is HttpRequestException or IOException)
        {
            return new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable,
                $"The Docker endpoint '{Endpoint.Display}' could not be reached.", exception);
        }

        return new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
            $"The Docker daemon at '{Endpoint.Display}' could not be used: {exception.Message}", exception);
    }

    private static SocketException? FindSocketException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException)
            {
                return socketException;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    var found = FindSocketException(inner);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}
