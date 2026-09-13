namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;

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
internal sealed class DockerDotNetRuntimeClient : IContainerRuntime
{
    /// <summary>How long the write probe may take before "the probe did not answer" becomes its answer.</summary>
    internal static readonly TimeSpan WriteProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Captured-output ceiling for the write probe, whose output is a diagnostic and never a result.</summary>
    private const int WriteProbeCaptureBytes = 4 * 1024;

    /// <summary>The per-read buffer for an exec stream. Bounds one read, not the capture; the ceiling does that.</summary>
    private const int ExecReadBufferBytes = 8 * 1024;

    private readonly DockerClient _client;
    private readonly TimeSpan _probeTimeout;
    private readonly TimeSpan _pullTimeout;
    private readonly ILogger _logger;
    private int _unrecognisedHealthLogged;

    /// <param name="endpoint">The daemon endpoint this client talks to.</param>
    /// <param name="probeTimeout">Bounds <see cref="ProbeAsync" />, and floors the transport timeout.</param>
    /// <param name="requestTimeout">
    ///     The HTTP request timeout. Separate from the probe timeout because the two consumers have opposite time
    ///     budgets: a ten-second transport timeout sized for a preflight would cut off a thirty-second graceful stop.
    ///     Null leaves it equal to <paramref name="probeTimeout" />, so the existing two-argument call sites behave
    ///     exactly as they did.
    /// </param>
    /// <param name="pullTimeout">
    ///     Deadline for one image pull, applied as a linked cancellation rather than as a transport timeout: an
    ///     application image takes minutes to fetch and the transport must not be sized for it.
    /// </param>
    /// <param name="logger">
    ///     Used for exactly two things: the once-per-client warning about a health state this engine does not
    ///     recognise, and a debug line naming a failed write probe. Optional so the existing call sites are unchanged.
    /// </param>
    public DockerDotNetRuntimeClient(DockerDaemonEndpoint endpoint,
        TimeSpan probeTimeout,
        TimeSpan? requestTimeout = null,
        TimeSpan? pullTimeout = null,
        ILogger? logger = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _probeTimeout = probeTimeout;
        _pullTimeout = pullTimeout ?? TimeSpan.FromMinutes(30);
        _logger = logger ?? NullLogger.Instance;
        _client = new DockerClientBuilder().WithEndpoint(endpoint.Uri).WithTimeout(requestTimeout ?? probeTimeout).Build();
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
                Endpoint,
                IsRootless(info),
                HasSecurityOption(info, "seccomp"));
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

    public async Task<IReadOnlyList<string>> ListContainersAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            // An empty filter would list every container on the daemon, and the caller removes what this returns.
            throw new ArgumentException("Listing containers without a label filter is refused: the result is used to remove containers.",
                nameof(labels));
        }

        try
        {
            var parameters = new ContainersListParameters
            {
                // Stopped containers too: an engine killed mid-run leaves an exited container behind that still holds
                // its name, so a sweep that only saw running ones would leave the next create failing on the conflict.
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
                {
                    ["label"] = labels.ToDictionary(pair => pair.Key + "=" + pair.Value, _ => true, StringComparer.Ordinal)
                }
            };

            var listed = await _client.Containers.ListContainersAsync(parameters, cancellationToken).ConfigureAwait(false);
            return [.. listed.Select(container => container.ID)];
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
                         .RemoveContainerAsync(containerId, new ContainerRemoveParameters
                         {
                             Force = true,
                             RemoveVolumes = true
                         }, cancellationToken)
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
                // Attached only when there is something to send. An exec with stdin attached and nothing written stays
                // open on the child's read until the connection is torn down, so attaching unconditionally would turn
                // every ordinary command into one that waits for input nobody is going to send.
                AttachStdin = request.StandardInput is not null,
                TTY = false,
                Cmd = [request.Executable, .. request.Arguments],
                WorkingDir = request.WorkingDirectory ?? string.Empty,
                Env = request.Environment?.Select(pair => pair.Key + "=" + pair.Value).ToArray() ?? []
            };

            var created = await _client.Exec.CreateContainerExecAsync(containerId, parameters, cancellationToken).ConfigureAwait(false);
            using var stream = await _client.Exec
                                            .StartContainerExecAsync(created.ID, new ContainerExecStartParameters
                                            {
                                                Detach = false,
                                                TTY = false
                                            }, cancellationToken)
                                            .ConfigureAwait(false);

            var captured = await PumpAsync(stream, request.StandardInput, request.MaxCapturedBytes, cancellationToken).ConfigureAwait(false);
            var inspected = await _client.Exec.InspectContainerExecAsync(created.ID, cancellationToken).ConfigureAwait(false);

            var (outputText, outputTruncated, errorText, errorTruncated) = captured;

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

    // ---------------------------------------------------------------------------------------------------------
    // IContainerRuntime — the application-container surface. Everything below is additive: no member above this
    // line changed, so a Development Mode create still produces byte-identical wire parameters.
    // ---------------------------------------------------------------------------------------------------------

    public async Task<string> RunContainerAsync(ContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);

        // Guards first, before a single parameter is built. `required` means "assigned", not "non-empty": a blank
        // host IP renders as a binding the daemon resolves to 0.0.0.0, and a blank user is not the same instruction
        // as no user. There must be no window in which such a container exists for a read-back to catch.
        if (specification.PublishedPorts.FirstOrDefault(publication => !IsLoopback(publication.HostIp)) is { } offender)
        {
            throw new ArgumentException($"Container port {offender.ContainerPort}/{offender.Protocol} asks to publish on host interface "
                                        + $"'{offender.HostIp}'. Application containers publish on 127.0.0.1 and on nothing else.",
                nameof(specification));
        }

        if (!ContainerImageReference.IsContentAddressed(specification.Image))
        {
            throw new ArgumentException($"Image '{specification.Image}' is not digest-pinned. A tag names whatever the registry last pushed, "
                                        + "not the bytes the catalog approved; give a 'name@sha256:<digest>' reference or a bare image id.",
                nameof(specification));
        }

        if (specification.User is not null && string.IsNullOrWhiteSpace(specification.User))
        {
            throw new ArgumentException("A blank container user is refused. Pass null for the image's own default user; \"\" and null would "
                                        + "otherwise be the same instruction written two ways and only one of them says what it means.",
                nameof(specification));
        }

        // Container port plus protocol is the key of both wire dictionaries below, so two publications sharing one
        // would throw the BCL's own duplicate-key ArgumentException from ToDictionary — raised outside the try, so
        // unclassified, and naming neither the port nor the specification. This layer says which publication instead.
        if (specification.PublishedPorts.GroupBy(PortKey, StringComparer.Ordinal)
                         .FirstOrDefault(group => group.Skip(1).Any()) is { } duplicated)
        {
            throw new ArgumentException($"Container port {duplicated.Key} is published more than once. A container port and protocol name one "
                                        + "exposed port on the daemon, so the second publication would replace the first rather than add to "
                                        + "it. Publish each container port once, on one host port.",
                nameof(specification));
        }

        var parameters = new CreateContainerParameters
        {
            Image = specification.Image,
            Name = specification.Name,
            Labels = specification.Labels.ToDictionary(StringComparer.Ordinal),
            Env = [.. specification.Environment.Select(pair => pair.Key + "=" + pair.Value)],
            ExposedPorts = specification.PublishedPorts.ToDictionary(PortKey, _ => default(EmptyStruct), StringComparer.Ordinal),
            // Exactly one endpoint: the daemon rejects a create that names more than one network.
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>(StringComparer.Ordinal)
                {
                    [specification.NetworkName] = new()
                    {
                        Aliases = [.. specification.NetworkAliases]
                    }
                }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = specification.NetworkName,
                Privileged = false,
                CapDrop = [.. specification.CapabilitiesToDrop],
                CapAdd = [.. specification.CapabilitiesToAdd],
                SecurityOpt = [.. specification.SecurityOptions],
                ReadonlyRootfs = specification.ReadOnlyRootFilesystem,
                Mounts = [.. specification.Mounts.Select(ToMount)],
                PortBindings = specification.PublishedPorts.ToDictionary(PortKey,
                    publication => (IList<PortBinding>)
                    [
                        new PortBinding
                        {
                            HostIP = publication.HostIp,
                            // The empty string, never "0", is how the daemon is asked to assign a port: "0" is a
                            // request to bind port zero.
                            HostPort = publication.HostPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty
                        }
                    ],
                    StringComparer.Ordinal),
                PublishAllPorts = false,
                ExtraHosts = [.. specification.ExtraHosts],
                RestartPolicy = new RestartPolicy
                {
                    Name = specification.RestartMode == ContainerRestartMode.UnlessStopped
                        ? RestartPolicyKind.UnlessStopped
                        : RestartPolicyKind.No
                },
                Memory = specification.MemoryBytes,
                NanoCPUs = specification.NanoCpus,
                PidsLimit = specification.PidsLimit,
                // Unconditional, and not conditional on a manifest flag: ADR 0010 makes GPU device requests a
                // non-goal, so there is no path through this method that can ask for one.
                Devices = [],
                DeviceRequests = [],
                IpcMode = "private",
                AutoRemove = false
            }
        };

        if (specification.User is not null)
        {
            parameters.User = specification.User;
        }

        if (specification.Entrypoint is not null)
        {
            parameters.Entrypoint = [.. specification.Entrypoint];
        }

        if (specification.Command is not null)
        {
            parameters.Cmd = [.. specification.Command];
        }

        if (specification.WorkingDirectory is not null)
        {
            parameters.WorkingDir = specification.WorkingDirectory;
        }

        if (specification.Healthcheck is { } healthcheck)
        {
            parameters.Healthcheck = new HealthcheckConfig
            {
                Test = [.. healthcheck.Test],
                Interval = healthcheck.Interval,
                Timeout = healthcheck.Timeout,
                Retries = healthcheck.Retries,
                StartPeriod = healthcheck.StartPeriod
            };
        }

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

    public async Task<ContainerInspection> InspectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            var inspected = await _client.Containers.InspectContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
            return ToInspection(containerId, inspected);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not DockerRuntimeException)
        {
            throw Classify(exception);
        }
    }

    /// <summary>
    ///     The inspect wire mapping, separated from the call so it can be driven without a daemon. Internal as a test
    ///     seam via <c>InternalsVisibleTo</c>; not part of the public contract.
    /// </summary>
    internal ContainerInspection ToInspection(string containerId, ContainerInspectResponse inspected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(inspected);

        var hostConfig = inspected.HostConfig
                         ?? throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                             $"The Docker daemon returned no host configuration for container '{containerId}', so its settings cannot be verified.");

        return new ContainerInspection
        {
            ContainerId = inspected.ID,
            // The daemon reports the name with a leading slash; the caller compares it against the name it asked
            // for, and a comparison that fails on a punctuation mark is not a verification.
            Name = (inspected.Name ?? string.Empty).TrimStart('/'),
            // Config.Image, not the response's top-level Image: that one is the resolved image ID
            // ('sha256:...'), while the caller verifies the container was created from the reference it pinned.
            // The fake seam answers with the requested reference, so reading the ID here would make the two
            // implementations disagree about what the field means.
            Image = inspected.Config?.Image ?? string.Empty,
            Labels = inspected.Config?.Labels is { } labels
                ? new Dictionary<string, string>(labels, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            State = ToRunState(inspected.State),
            RequestedPortBindings = ToRequestedPortBindings(hostConfig.PortBindings),
            PublishedPorts = ToPublishedPorts(inspected.NetworkSettings?.Ports),
            User = inspected.Config?.User ?? string.Empty,
            NetworkMode = hostConfig.NetworkMode ?? string.Empty,
            Privileged = hostConfig.Privileged,
            ReadOnlyRootFilesystem = hostConfig.ReadonlyRootfs,
            CapabilitiesDropped = hostConfig.CapDrop?.ToArray() ?? [],
            CapabilitiesAdded = hostConfig.CapAdd?.ToArray() ?? [],
            SecurityOptions = hostConfig.SecurityOpt?.ToArray() ?? [],
            // The response's TOP-LEVEL Mounts, not HostConfig.Mounts: only the effective set carries an anonymous
            // volume the image's own VOLUME instruction created, which is exactly the mount that would put
            // application data outside the instance directory unnoticed.
            Mounts = inspected.Mounts?.Select(ToMountView).ToArray() ?? [],
            DeviceCount = (hostConfig.Devices?.Count ?? 0) + (hostConfig.DeviceRequests?.Count ?? 0),
            PidMode = hostConfig.PidMode ?? string.Empty,
            IpcMode = hostConfig.IpcMode ?? string.Empty,
            UtsMode = hostConfig.UTSMode ?? string.Empty,
            MemoryBytes = hostConfig.Memory,
            NanoCpus = hostConfig.NanoCPUs,
            PidsLimit = hostConfig.PidsLimit ?? 0,
            RestartMode = hostConfig.RestartPolicy?.Name == RestartPolicyKind.UnlessStopped
                ? ContainerRestartMode.UnlessStopped
                : ContainerRestartMode.None,
            ExtraHosts = hostConfig.ExtraHosts?.ToArray() ?? []
        };
    }

    public async Task<bool> StopContainerAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);

        try
        {
            return await _client.Containers
                                .StopContainerAsync(containerId,
                                    new ContainerStopParameters
                                    {
                                        WaitBeforeKillSeconds = (uint)Math.Clamp(gracePeriod.TotalSeconds, 0, 600)
                                    },
                                    cancellationToken)
                                .ConfigureAwait(false);
        }
        catch (DockerContainerNotFoundException)
        {
            // Already gone, which is the same answer as "it was not running": false, not an error. Matches the
            // idempotence RemoveContainerAsync already promises, so a teardown never has to reason about how far a
            // previous attempt got.
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<bool> ImageExistsAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        try
        {
            await _client.Images.InspectImageAsync(imageReference, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DockerImageNotFoundException)
        {
            return false;
        }
        catch (DockerApiException apiException) when (apiException.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task PullImageAsync(string imageReference,
        IProgress<ContainerPullProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        var separator = imageReference.IndexOf("@sha256:", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ArgumentException($"Image '{imageReference}' is not digest-pinned. This layer never pulls a tag: a tag names whatever "
                                        + "the registry last pushed, not the bytes the catalog approved.",
                nameof(imageReference));
        }

        var aggregator = new PullProgressAggregator(imageReference, TimeProvider.System);
        var sink = new PullProgressSink(aggregator, progress);

        // Its own deadline rather than the transport's: an application image is large, and a pull sized by the
        // ten-second timeout a preflight needs would fail on every first install.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_pullTimeout);

        try
        {
            await _client.Images
                         .CreateImageAsync(new ImagesCreateParameters
                             {
                                 FromImage = imageReference[..separator],
                                 Tag = imageReference[(separator + 1)..]
                             },
                             authConfig: null,
                             sink,
                             deadline.Token)
                         .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                $"The pull of '{imageReference}' did not complete within {_pullTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }

        // A pull that failed part-way still completes the HTTP call normally and reports the failure inside the
        // stream, so this read is the difference between a named failure and a silent half-pull.
        if (aggregator.Error is { } error)
        {
            throw new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
                $"The pull of '{imageReference}' failed: {error}");
        }

        progress?.Report(aggregator.Snapshot());
    }

    public async Task<string> CreateNetworkAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("A container network specification must carry a name.", nameof(specification));
        }

        // Before the create, not after the conflict. The labels ARE the ownership proof this layer reuses a network
        // on: with none of them, OwnsNetwork has nothing to compare and a foreign bridge that happens to hold the
        // name passes, putting the application on someone else's network. A specification that cannot prove
        // ownership is refused rather than given a check it is guaranteed to pass.
        if (specification.Labels.Count == 0)
        {
            throw new ArgumentException($"The container network '{specification.Name}' carries no labels. At least one ownership label is "
                                        + "required: a network is reused only when it provably belongs to this engine, and an empty label map "
                                        + "makes that check vacuous.",
                nameof(specification));
        }

        try
        {
            return await CreateNetworkOnceAsync(specification, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerApiException apiException) when (apiException.StatusCode == HttpStatusCode.Conflict)
        {
            // A name conflict says some network already holds that name. It does not say the network is ours, and a
            // name is not a capability: attaching the application to a network somebody else created would put it on
            // a bridge with everything else attached to it. So reuse needs proof of ownership, and nothing else does.
            NetworkResponse existing;
            try
            {
                existing = await _client.Networks.InspectNetworkAsync(specification.Name, cancellationToken).ConfigureAwait(false);
            }
            catch (DockerNetworkNotFoundException)
            {
                // Removed in the gap between the conflict and the inspect. One retry, then it fails classified.
                try
                {
                    return await CreateNetworkOnceAsync(specification, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception retryFailure) when (retryFailure is not OperationCanceledException)
                {
                    throw Classify(retryFailure);
                }
            }
            catch (Exception inspectFailure) when (inspectFailure is not OperationCanceledException)
            {
                throw Classify(inspectFailure);
            }

            if (!OwnsNetwork(existing, specification))
            {
                throw new ContainerPolicyException(ContainerPolicyException.ForeignNetworkReason,
                    $"The network name '{specification.Name}' is in use by a foreign container network "
                    + $"(id '{existing.ID}', driver '{existing.Driver}'). It was not created by this application "
                    + "instance, so it is not reused.");
            }

            return existing.ID;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task RemoveNetworkAsync(string networkNameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkNameOrId);

        try
        {
            await _client.Networks.DeleteNetworkAsync(networkNameOrId, cancellationToken).ConfigureAwait(false);
        }
        catch (DockerNetworkNotFoundException)
        {
            // Already gone, and that is the outcome the caller wanted. A network that still has containers attached
            // is a different matter: it fails classified, which is what makes teardown ordering observable.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<IReadOnlyList<string>> ListNetworksAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            throw new ArgumentException("Listing networks without a label filter is refused: the result is used to remove networks.",
                nameof(labels));
        }

        try
        {
            var listed = await _client.Networks
                                      .ListNetworksAsync(new NetworksListParameters
                                      {
                                          Filters = LabelFilter(labels)
                                      }, cancellationToken)
                                      .ConfigureAwait(false);

            return [.. listed.Select(network => network.ID)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    // The daemon's own state vocabulary is lower-case ("running", "exited"), and this value is compared against
    // those literals rather than displayed, so upper-casing it would mean normalising away from the wire format the
    // comparison is against.
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Docker's container state words are lower-case on the wire and are compared, not displayed.")]
    public async Task<IReadOnlyList<ContainerSummary>> ListContainersDetailedAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (labels.Count == 0)
        {
            throw new ArgumentException("Listing containers without a label filter is refused: the result drives lifecycle decisions.",
                nameof(labels));
        }

        try
        {
            var listed = await _client.Containers
                                      .ListContainersAsync(new ContainersListParameters
                                      {
                                          // The whole point: the default lists running containers only, so a container
                                          // the user stopped through `docker stop` would look removed rather than
                                          // stopped, and the observer would report a crashed application as gone.
                                          All = true,
                                          Filters = LabelFilter(labels)
                                      }, cancellationToken)
                                      .ConfigureAwait(false);

            return
            [
                .. listed.Select(container => new ContainerSummary
                {
                    Id = container.ID,
                    Labels = container.Labels is { } containerLabels
                        ? new Dictionary<string, string>(containerLabels, StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal),
                    State = (container.State ?? string.Empty).ToLowerInvariant(),
                    ExitCode = ParseExitCode(container.Status)
                })
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<ContainerLogSnapshot> ReadLogsAsync(string containerId,
        ContainerLogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var stream = await _client.Containers
                                            .GetContainerLogsAsync(containerId,
                                                new ContainerLogsParameters
                                                {
                                                    ShowStdout = true,
                                                    ShowStderr = true,
                                                    Timestamps = false,
                                                    Follow = false,
                                                    // Both are strings on 4.3.3 — neither is a number and neither is a
                                                    // DateTime — so both are formatted invariantly rather than by the
                                                    // node's locale.
                                                    Tail = request.TailLines.ToString(CultureInfo.InvariantCulture),
                                                    Since = request.SinceUtc?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                                                },
                                                cancellationToken)
                                            .ConfigureAwait(false);

            var buffer = new byte[16 * 1024];
            var kept = new List<byte>(Math.Min(request.MaxBytes, 64 * 1024));
            var truncated = false;

            // Containers are created with TTY unset, so the daemon always frames the stream and it must be
            // demultiplexed. Both streams go into one builder in the order the daemon framed them, which is the order
            // they happened.
            while (true)
            {
                var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read.EOF)
                {
                    break;
                }

                if (read.Count == 0)
                {
                    continue;
                }

                var room = request.MaxBytes - kept.Count;
                if (read.Count > room)
                {
                    // Strictly greater, so a read that lands exactly on the ceiling is not reported as truncated:
                    // the next read decides, and a caller shown "truncated" for a complete log would go looking for
                    // bytes that were never dropped.
                    kept.AddRange(buffer.AsSpan(0, room));
                    truncated = true;
                    break;
                }

                kept.AddRange(buffer.AsSpan(0, read.Count));
            }

            // Encoding.UTF8 decodes with a replacement fallback, so a ceiling that cut through a multi-byte sequence
            // yields U+FFFD rather than throwing: a bounded read is a policy, not a data-integrity failure.
            var text = Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(kept));

            return new ContainerLogSnapshot
            {
                Text = text,
                Truncated = truncated,
                LineCount = text.AsSpan().Count('\n')
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    public async Task<bool> ProbeWritablePathAsync(string containerId, string containerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(WriteProbeTimeout);

        try
        {
            var parameters = new ContainerExecCreateParameters
            {
                AttachStdout = true,
                AttachStderr = true,
                AttachStdin = false,
                TTY = false,
                // The path travels as $1, so a container path holding a space or a quote is data rather than shell
                // syntax. The command is fixed and takes nothing from the caller: this member exists precisely so
                // that no consumer of this layer ever gets to choose what runs inside a container.
                Cmd = ["sh", "-c", "f=\"$1/.xe-write-probe-$$\"; : > \"$f\" && rm -f \"$f\"", "_", containerPath],
                WorkingDir = string.Empty,
                Env = []
            };

            var created = await _client.Exec.CreateContainerExecAsync(containerId, parameters, deadline.Token).ConfigureAwait(false);
            using var stream = await _client.Exec
                                            .StartContainerExecAsync(created.ID,
                                                new ContainerExecStartParameters
                                                {
                                                    Detach = false,
                                                    TTY = false
                                                },
                                                deadline.Token)
                                            .ConfigureAwait(false);

            var captured = await PumpAsync(stream, standardInput: null, WriteProbeCaptureBytes, deadline.Token).ConfigureAwait(false);
            var inspected = await _client.Exec.InspectContainerExecAsync(created.ID, deadline.Token).ConfigureAwait(false);
            var exitCode = inspected.ExitCode ?? -1;

            if (exitCode != 0)
            {
                var output = captured.StandardOutput + captured.StandardError;
                _logger.LogDebug("Write probe of '{ContainerPath}' in container {ContainerId} exited {ExitCode}: {Output}",
                    containerPath,
                    containerId,
                    exitCode,
                    output);
            }

            return exitCode == 0;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // "The probe did not answer" is the same install-blocking answer as "the mount is not writable", so it is
            // returned rather than thrown: a caller that had to catch a timeout here would have two ways to say no.
            _logger.LogDebug("Write probe of '{ContainerPath}' in container {ContainerId} did not answer within {Seconds} seconds.",
                containerPath,
                containerId,
                WriteProbeTimeout.TotalSeconds);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Classify(exception);
        }
    }

    /// <summary>
    ///     The one loopback literal, compared ordinally: a host IP is a wire value, not localized text.
    ///     <para>
    ///         <c>::1</c> is deliberately NOT accepted. An application's published port is reached by this node's own
    ///         processes and by the operator's browser, both of which are given a <c>127.0.0.1</c> address, and a
    ///         daemon asked to publish on the IPv6 loopback binds a socket nothing here connects to — a port that
    ///         reads back as published and answers nobody. One address is also one thing for a later slice's port
    ///         allocator to reserve and one thing for a policy check to compare against.
    ///     </para>
    /// </summary>
    private static bool IsLoopback(string hostIp)
    {
        return string.Equals(hostIp, "127.0.0.1", StringComparison.Ordinal);
    }

    private static string PortKey(ContainerPortPublication publication)
    {
        return publication.ContainerPort.ToString(CultureInfo.InvariantCulture) + "/" + publication.Protocol;
    }

    /// <summary>
    ///     Both dictionary levels are constructed explicitly. <c>Filters</c> is interface-typed and defaults to null on
    ///     4.3.3, so the nested collection-initializer form dereferences null at run time rather than failing to
    ///     compile.
    /// </summary>
    private static Dictionary<string, IDictionary<string, bool>> LabelFilter(IReadOnlyDictionary<string, string> labels)
    {
        var byLabel = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            byLabel[key + "=" + value] = true;
        }

        return new Dictionary<string, IDictionary<string, bool>>(StringComparer.Ordinal)
        {
            ["label"] = byLabel
        };
    }

    private async Task<string> CreateNetworkOnceAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken)
    {
        var created = await _client.Networks
                                   .CreateNetworkAsync(new NetworksCreateParameters
                                   {
                                       Name = specification.Name,
                                       Driver = "bridge",
                                       Internal = specification.Internal,
                                       Attachable = false,
                                       EnableIPv6 = false,
                                       // Assigned rather than initialized into: Labels is interface-typed and null by
                                       // default. CheckDuplicate is deliberately absent — it does not exist in 4.3.3.
                                       Labels = new Dictionary<string, string>(specification.Labels, StringComparer.Ordinal)
                                   }, cancellationToken)
                                   .ConfigureAwait(false);

        return created.ID;
    }

    /// <summary>
    ///     Whether an existing network of the same name is provably this specification's own: the <c>bridge</c> driver,
    ///     and every label the specification asked for present with the same value. The instance and install ids the
    ///     caller puts in that label map are therefore a security input, not bookkeeping.
    /// </summary>
    private static bool OwnsNetwork(NetworkResponse existing, ContainerNetworkSpecification specification)
    {
        if (!string.Equals(existing.Driver, "bridge", StringComparison.Ordinal))
        {
            return false;
        }

        if (existing.Labels is not { } labels)
        {
            // An unlabelled network can never carry this specification's labels, and CreateNetworkAsync refuses a
            // specification that has none — so there is no "both empty" case that could make this true.
            return false;
        }

        foreach (var (key, value) in specification.Labels)
        {
            if (!labels.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private ContainerRunState ToRunState(State? state)
    {
        return new ContainerRunState
        {
            Running = state?.Running ?? false,
            Status = state?.Status ?? string.Empty,
            ExitCode = state?.ExitCode ?? 0,
            OutOfMemoryKilled = state?.OOMKilled ?? false,
            Health = ToHealthState(state?.Health?.Status),
            StartedAtUtc = ParseTimestamp(state?.StartedAt),
            FinishedAtUtc = ParseTimestamp(state?.FinishedAt)
        };
    }

    /// <summary>
    ///     Maps the daemon's health word. A null status means no healthcheck was declared and is silent; a status this
    ///     engine does not recognise is <see cref="ContainerHealthState.None" /> AND one warning carrying the value,
    ///     emitted once per client so a renamed daemon state is visible rather than silent. The caller reads
    ///     <c>None</c> from a service that declared a healthcheck as "not yet healthy", so an unrecognised word delays
    ///     to the deadline rather than passing as healthy.
    /// </summary>
    // Internal rather than private so a unit test can pin the mapping and the once-per-client warning: the only
    // other way in is InspectAsync, which needs a daemon that can be asked to report a state it does not have.
    internal ContainerHealthState ToHealthState(string? status)
    {
        if (status is null)
        {
            return ContainerHealthState.None;
        }

        if (status.Equals("starting", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthState.Starting;
        }

        if (status.Equals("healthy", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthState.Healthy;
        }

        if (status.Equals("unhealthy", StringComparison.OrdinalIgnoreCase))
        {
            return ContainerHealthState.Unhealthy;
        }

        if (Interlocked.Exchange(ref _unrecognisedHealthLogged, 1) == 0)
        {
            _logger.LogWarning("The Docker daemon reported container health state '{HealthStatus}', which this engine does not "
                               + "recognise. It is treated as not yet healthy.",
                status);
        }

        return ContainerHealthState.None;
    }

    /// <summary>
    ///     The daemon renders these as RFC 3339 strings and uses <c>0001-01-01T00:00:00Z</c> for "never", which must
    ///     read as null: a caller shown a start time of year one would render it rather than omit it.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return null;
        }

        return parsed == default ? null : parsed;
    }

    /// <summary>
    ///     What the daemon was <em>asked</em> to bind. Present from create onwards, and a blank host port maps to null
    ///     rather than dropping the entry: "daemon-assigned" is a request the pre-start check has to see.
    /// </summary>
    private static IReadOnlyList<ContainerPortPublication> ToRequestedPortBindings(IDictionary<string, IList<PortBinding>>? bindings)
    {
        if (bindings is null)
        {
            return [];
        }

        var requested = new List<ContainerPortPublication>();
        foreach (var (key, bound) in bindings)
        {
            if (!TryParsePortKey(key, out var containerPort, out var protocol) || bound is null)
            {
                continue;
            }

            foreach (var binding in bound)
            {
                requested.Add(new ContainerPortPublication
                {
                    ContainerPort = containerPort,
                    Protocol = protocol,
                    HostIp = binding.HostIP ?? string.Empty,
                    HostPort = int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort)
                        ? hostPort
                        : null
                });
            }
        }

        return requested;
    }

    /// <summary>
    ///     What the daemon actually bound. Empty before start, which is why the request side exists; an entry whose
    ///     host port is blank is not a published port and is skipped.
    /// </summary>
    private static IReadOnlyList<ContainerPublishedPort> ToPublishedPorts(IDictionary<string, IList<PortBinding>>? ports)
    {
        if (ports is null)
        {
            return [];
        }

        var published = new List<ContainerPublishedPort>();
        foreach (var (key, bound) in ports)
        {
            if (!TryParsePortKey(key, out var containerPort, out var protocol) || bound is null)
            {
                continue;
            }

            foreach (var binding in bound)
            {
                if (!int.TryParse(binding.HostPort, NumberStyles.None, CultureInfo.InvariantCulture, out var hostPort))
                {
                    continue;
                }

                published.Add(new ContainerPublishedPort
                {
                    ContainerPort = containerPort,
                    Protocol = protocol,
                    HostIp = binding.HostIP ?? string.Empty,
                    HostPort = hostPort
                });
            }
        }

        return published;
    }

    private static bool TryParsePortKey(string key, out int containerPort, out string protocol)
    {
        containerPort = 0;
        protocol = "tcp";

        var separator = key.IndexOf('/', StringComparison.Ordinal);
        var portText = separator < 0 ? key : key[..separator];
        if (separator >= 0)
        {
            protocol = key[(separator + 1)..];
        }

        return int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out containerPort);
    }

    /// <summary>
    ///     The exit code of a listed container, read out of the daemon's own status prose (<c>Exited (137) 3 minutes
    ///     ago</c>): 4.3.3's list response carries no exit-code member. Null on any other shape, and null means "the
    ///     daemon did not say" — never <c>0</c>, which would report a crashed application as a clean exit.
    /// </summary>
    // Internal rather than private so a unit test can pin the shapes: this reads a number out of daemon prose, and
    // the real-daemon test proves the prose while this proves the parser.
    internal static int? ParseExitCode(string? status)
    {
        if (string.IsNullOrEmpty(status) || !status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var open = status.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return null;
        }

        var close = status.IndexOf(')', open + 1);
        if (close < 0)
        {
            return null;
        }

        return int.TryParse(status.AsSpan(open + 1, close - open - 1),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var exitCode)
            ? exitCode
            : null;
    }

    private static Mount ToMount(ContainerMount mount)
    {
        return new Mount
        {
            Type = "bind",
            Source = mount.HostPath,
            Target = mount.ContainerPath,
            ReadOnly = mount.ReadOnly,
            BindOptions = new BindOptions
            {
                Propagation = mount.Propagation
            }
        };
    }

    private static ContainerMountView ToMountView(MountPoint mount)
    {
        return new ContainerMountView
        {
            Type = mount.Type ?? string.Empty,
            Source = mount.Source ?? string.Empty,
            Destination = mount.Destination ?? string.Empty,
            // RW is the daemon's own word for the effective set and is non-nullable here, so read-only is its negation
            // rather than a separate absent-means-writable rule.
            ReadOnly = !mount.RW
        };
    }

    /// <summary>
    ///     Feeds the daemon's message stream through the aggregator and relays only what the throttle lets out.
    ///     A hand-written sink rather than <see cref="Progress{T}" />, which posts to a synchronization context and
    ///     would reorder or defer the reports relative to the pull that produced them.
    /// </summary>
    private sealed class PullProgressSink : IProgress<JSONMessage>
    {
        private readonly PullProgressAggregator _aggregator;
        private readonly IProgress<ContainerPullProgress>? _downstream;

        public PullProgressSink(PullProgressAggregator aggregator, IProgress<ContainerPullProgress>? downstream)
        {
            _aggregator = aggregator;
            _downstream = downstream;
        }

        public void Report(JSONMessage value)
        {
            // Folded even when nobody is listening: the aggregator is also where the stream's error is captured, and
            // a caller that passed no progress still needs the pull to fail rather than half-succeed.
            var progress = _aggregator.Report(value);
            if (progress is not null)
            {
                _downstream?.Report(progress);
            }
        }
    }

    /// <summary>
    ///     Drives both directions of one exec stream and returns its captured output.
    ///     <para>
    ///         The payload goes up WHILE the output is drained, not before it. A Docker exec is a single bidirectional
    ///         connection: with nothing reading, a child that writes as it reads fills the daemon's buffer, the daemon
    ///         stops accepting the payload, and the two sides wait on each other until the command times out.
    ///         Development Mode's patch ceiling is 8 MB, comfortably past where a serialised version stops working.
    ///     </para>
    ///     <para>
    ///         The half-close after the write is equally load-bearing: a child reading to end-of-input — <c>git apply</c>
    ///         taking its patch from standard input is the case that matters — never returns while the write side is
    ///         open, so without <c>CloseWrite</c> the command hangs rather than completes.
    ///     </para>
    /// </summary>
    private static async Task<ExecOutput> PumpAsync(MultiplexedStream stream,
        string? standardInput,
        int maxBytesPerStream,
        CancellationToken cancellationToken)
    {
        if (standardInput is null)
        {
            return await ReadBoundedAsync(stream, maxBytesPerStream, cancellationToken).ConfigureAwait(false);
        }

        var payload = Encoding.UTF8.GetBytes(standardInput);
        var writeTask = SendAsync(stream, payload, cancellationToken);
        try
        {
            return await ReadBoundedAsync(stream, maxBytesPerStream, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Awaited inside this method, so the stream outlives both halves: a write still in flight when the caller
            // disposed the stream would be a use-after-dispose rather than a tidy-up detail.
            await writeTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Drain an exec stream to its end, keeping at most <paramref name="maxBytesPerStream" /> bytes of each half
    ///     and discarding the rest as it arrives.
    ///     <para>
    ///         The ceiling has to be applied DURING the read, not after it. What is on the other end of this stream is
    ///         a process inside an image the engine did not build, and <c>ReadOutputToEndAsync</c> accumulates
    ///         everything it sends before anyone gets to truncate it — so a container that writes a gigabyte decides
    ///         how much of this node's memory it uses, and the capture ceiling only decides how much of that is then
    ///         thrown away. The stream is still drained to the end rather than abandoned: the caller's deadline is
    ///         what bounds the time, and stopping the read early would leave the daemon writing into a connection
    ///         nobody is reading.
    ///     </para>
    /// </summary>
    internal static async Task<ExecOutput> ReadBoundedAsync(MultiplexedStream stream,
        int maxBytesPerStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerStream);

        var buffer = new byte[ExecReadBufferBytes];
        var standardOutput = new BoundedCapture(maxBytesPerStream);
        var standardError = new BoundedCapture(maxBytesPerStream);

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, offset: 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read.EOF)
            {
                break;
            }

            var target = read.Target == MultiplexedStream.TargetStream.StandardError ? standardError : standardOutput;
            target.Append(buffer, read.Count);
        }

        return new ExecOutput(standardOutput.Text, standardOutput.Truncated, standardError.Text, standardError.Truncated);
    }

    private static async Task SendAsync(MultiplexedStream stream, byte[] payload, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(payload, offset: 0, payload.Length, cancellationToken).ConfigureAwait(false);
        stream.CloseWrite();
    }

    /// <summary>Whether the daemon runs rootless, read off the same <c>SecurityOptions</c> list <c>docker info</c> prints.</summary>
    private static bool IsRootless(SystemInfoResponse info)
    {
        return HasSecurityOption(info, "rootless");
    }

    /// <summary>
    ///     Whether <c>docker info</c> lists <paramref name="name" /> among the daemon's security options. Matched on
    ///     the <c>name=</c> key rather than on the whole entry, because the daemon renders these as comma-separated
    ///     key/value groups (<c>name=seccomp,profile=builtin</c>) and only the name is stable.
    /// </summary>
    private static bool HasSecurityOption(SystemInfoResponse info, string name)
    {
        return info.SecurityOptions?.Any(option => option
                                                   .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                   .Any(part => part.Equals("name=" + name, StringComparison.OrdinalIgnoreCase)))
               ?? false;
    }

    private static Mount ToMount(DockerBindMount bindMount)
    {
        return new Mount
        {
            Type = "bind",
            Source = bindMount.HostPath,
            Target = bindMount.ContainerPath,
            ReadOnly = bindMount.ReadOnly,
            BindOptions = new BindOptions
            {
                Propagation = bindMount.Propagation
            }
        };
    }

    private static DockerBindMount FromMount(Mount mount)
    {
        return new DockerBindMount
        {
            HostPath = mount.Source ?? string.Empty,
            ContainerPath = mount.Target ?? string.Empty,
            // A read-write mount comes back with ReadOnly absent rather than false (measured against a rootless Docker Engine),
            // so a null must read as "writable" — reading it as "unknown" would fail the read-only check on every
            // ordinary workspace mount.
            ReadOnly = mount.ReadOnly ?? false,
            Propagation = mount.BindOptions?.Propagation ?? string.Empty
        };
    }

    // Both captured streams of one exec, each already bounded by the caller's per-stream capture ceiling.
    internal sealed record ExecOutput(string StandardOutput, bool StandardOutputTruncated, string StandardError, bool StandardErrorTruncated);

    /// <summary>
    ///     One captured stream, bounded: the first <c>maxBytes</c> bytes are kept and everything after them is
    ///     counted as dropped and discarded rather than buffered.
    /// </summary>
    private sealed class BoundedCapture
    {
        private readonly byte[] _kept;
        private int _length;

        public BoundedCapture(int maxBytes)
        {
            _kept = new byte[maxBytes];
        }

        public bool Truncated { get; private set; }

        // Decoded with the replacement fallback so a ceiling that cuts through a multi-byte sequence yields a
        // replacement character rather than throwing: a bounded capture is a policy, not a data-integrity claim.
        public string Text => Encoding.UTF8.GetString(_kept, index: 0, _length);

        public void Append(byte[] source, int count)
        {
            var copied = Math.Min(_kept.Length - _length, count);
            if (copied > 0)
            {
                Array.Copy(source, sourceIndex: 0, _kept, _length, copied);
                _length += copied;
            }

            if (copied < count)
            {
                Truncated = true;
            }
        }
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
