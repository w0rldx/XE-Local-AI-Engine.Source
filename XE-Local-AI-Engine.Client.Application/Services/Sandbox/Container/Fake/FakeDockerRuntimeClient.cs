namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Containers;

/// <summary>
///     A deterministic in-memory <see cref="IDockerRuntimeClient" /> for unit coverage of everything above the wire:
///     endpoint classification, daemon attestation, and — the reason it exists — the Docker hardening contract's fail-closed read-back.
///     <para>
///         Its defining feature is <see cref="SettingsMutator" />: a hook that rewrites the settings the fake reports
///         back from a "created" container. A real daemon cannot be asked to silently drop <c>--cap-drop ALL</c> or
///         to quietly ignore a memory ceiling, so without a client that can, the branch which refuses an unverifiable
///         container would never execute in a test. A fail-closed control whose failure path is untested is a
///         fail-closed control on paper only.
///     </para>
///     <para>
///         Production-resident by design, matching <c>FakeSandboxRuntimeProvider</c>: it is a configuration-selected
///         double rather than a test-project type, so the seam it exercises is the same seam production uses.
///     </para>
/// </summary>
public sealed class FakeDockerRuntimeClient : IContainerRuntime
{
    private readonly ConcurrentDictionary<string, ContainerRecord> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DockerExecutionOutcome> _scriptedCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ContainerNetworkSpecification> _networks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _networkIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TimeSpan> _stoppedGracePeriods = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _pulledImages = new();
    private readonly ConcurrentQueue<string> _removedNetworks = new();
    private readonly ConcurrentQueue<(string ContainerId, string ContainerPath)> _probedPaths = new();
    private int _containerCounter;
    private int _networkCounter;
    private int _hostPortCounter;

    public FakeDockerRuntimeClient(DockerDaemonEndpoint endpoint, DockerDaemonIdentity? identity = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Identity = identity ?? new DockerDaemonIdentity("fake-daemon", "99.0.0", "1.99", "1.40", "linux", endpoint, IsRootless: false, SupportsSeccomp: true);
    }

    /// <summary>
    ///     Whether a <c>touch</c> of a path under a bind mount really creates the file on the host side of that mount.
    ///     <para>
    ///         On by default because a conformant daemon does exactly this, and the provider's create-time mapping
    ///         probe depends on it — a fake that could not write through a mount would fail every create for a reason
    ///         that has nothing to do with what the test is about. Turning it off is how a test reproduces the
    ///         rootless-mapping failure this machine's daemon cannot be asked to produce on demand: a container that
    ///         reports success while nothing appears on the host.
    ///     </para>
    /// </summary>
    public bool WritesThroughBindMounts { get; set; } = true;

    /// <summary>Every <see cref="DockerExecutionRequest" /> this client was handed, in order.</summary>
    public IReadOnlyList<DockerExecutionRequest> ExecutedRequests => _executed.ToArray();

    private readonly ConcurrentQueue<DockerExecutionRequest> _executed = new();

    public DockerDaemonEndpoint Endpoint { get; }

    /// <summary>The identity <see cref="ProbeAsync" /> reports. Settable so an attestation-change test can move it.</summary>
    public DockerDaemonIdentity Identity { get; set; }

    /// <summary>When set, <see cref="ProbeAsync" /> throws this instead of answering.</summary>
    public DockerRuntimeException? ProbeFailure { get; set; }

    /// <summary>
    ///     Rewrites the settings reported by <see cref="InspectContainerAsync" />. Null means "report exactly what was
    ///     asked for", which is what a conformant daemon does.
    /// </summary>
    public Func<DockerContainerSettings, DockerContainerSettings>? SettingsMutator { get; set; }

    /// <summary>Container ids created through this client, in order. Lets a test assert the fail-closed path removed its container.</summary>
    public IReadOnlyList<string> CreatedContainerIds { get; private set; } = [];

    /// <summary>Container ids removed through this client, in order.</summary>
    public IReadOnlyList<string> RemovedContainerIds => _removed.ToArray();

    private readonly ConcurrentQueue<string> _removed = new();

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public Task<DockerDaemonIdentity> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ProbeFailure is not null ? Task.FromException<DockerDaemonIdentity>(ProbeFailure) : Task.FromResult(Identity);
    }

    public Task<string> CreateContainerAsync(DockerContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();

        var containerId = "fake-container-" + Interlocked.Increment(ref _containerCounter);
        _containers[containerId] = new ContainerRecord(specification);
        CreatedContainerIds = [.. CreatedContainerIds, containerId];
        return Task.FromResult(containerId);
    }

    public Task StartContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var record = GetRecord(containerId);

        // Assigned here and retained, the way a daemon assigns a host port when it binds one. Computing it per
        // inspection meant a container whose specification left the host port unset reported a DIFFERENT binding
        // every time it was read, so a caller that inspected twice — a post-start verification followed by a status
        // read — saw its own application move ports without anything having happened.
        if (record.ApplicationSpecification is { } specification)
        {
            record.PublishedPorts ??= PublishedPortsOf(specification);
            record.FinishedWith = OneShotExitCode?.Invoke(specification);
        }

        record.Started = true;
        record.EverStarted = true;
        return Task.CompletedTask;
    }

    public Task<DockerContainerSettings> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var specification = GetRecord(containerId).Specification
                            ?? throw new DockerRuntimeException($"Container '{containerId}' was not created through the Development Mode surface.");
        var conformant = new DockerContainerSettings
        {
            ContainerId = containerId,
            User = specification.User,
            NetworkMode = specification.NetworkMode,
            Privileged = false,
            ReadOnlyRootFilesystem = specification.ReadOnlyRootFilesystem,
            CapabilitiesDropped = specification.CapabilitiesToDrop,
            CapabilitiesAdded = [],
            SecurityOptions = specification.SecurityOptions,
            TemporaryFilesystems = specification.TemporaryFilesystems,
            Mounts = specification.BindMounts,
            MemoryBytes = specification.MemoryBytes,
            NanoCpus = specification.NanoCpus,
            PidsLimit = specification.PidsLimit,
            DeviceCount = 0,
            PidMode = string.Empty,
            IpcMode = "private",
            UtsMode = string.Empty
        };

        return Task.FromResult(SettingsMutator is null ? conformant : SettingsMutator(conformant));
    }

    /// <summary>
    ///     Applies the label filter the way the daemon does — every requested label must match — so a test can prove
    ///     the sweep passes a filter narrow enough to leave a foreign container alone.
    /// </summary>
    public Task<IReadOnlyList<string>> ListContainersAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> matches =
        [
            .. _containers
               .Where(entry => labels.All(filter => entry.Value.Labels.TryGetValue(filter.Key, out var value)
                                                    && string.Equals(value, filter.Value, StringComparison.Ordinal)))
               .Select(entry => entry.Key)
               .Order(StringComparer.Ordinal)
        ];

        return Task.FromResult(matches);
    }

    /// <summary>When set, <see cref="RemoveContainerAsync" /> throws it instead of removing. Lets a test prove one failed removal does not stop the sweep.</summary>
    public Func<string, DockerRuntimeException?>? RemovalFailure { get; set; }

    public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (RemovalFailure?.Invoke(containerId) is { } failure)
        {
            return Task.FromException(failure);
        }

        _containers.TryRemove(containerId, out _);
        _removed.Enqueue(containerId);
        return Task.CompletedTask;
    }

    public Task<DockerExecutionOutcome> ExecuteAsync(string containerId,
        DockerExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var record = GetRecord(containerId);
        _executed.Enqueue(request);

        var key = BuildCommandKey(request);
        if (_scriptedCommands.TryGetValue(key, out var scripted))
        {
            return Task.FromResult(scripted);
        }

        if (record.Specification is { } developmentSpecification)
        {
            TouchThroughBindMount(developmentSpecification, request);
        }

        return Task.FromResult(new DockerExecutionOutcome
        {
            ExitCode = 0,
            StandardOutput = string.Empty,
            StandardError = string.Empty,
            StandardOutputTruncated = false,
            StandardErrorTruncated = false
        });
    }

    /// <summary>
    ///     Emulates the one wire effect this fake cannot leave unmodelled: a <c>touch</c> of a path inside a bind mount
    ///     appears on the host side of that mount. Only <c>touch</c>, and only under a declared mount — everything else
    ///     stays a scripted outcome, because a fake that started really running commands would stop being a fake.
    /// </summary>
    private void TouchThroughBindMount(DockerContainerSpecification specification, DockerExecutionRequest request)
    {
        if (!WritesThroughBindMounts
            || !string.Equals(request.Executable, "touch", StringComparison.Ordinal)
            || request.Arguments.Count != 1)
        {
            return;
        }

        var containerPath = request.Arguments[0];
        foreach (var mount in specification.BindMounts)
        {
            var prefix = mount.ContainerPath.EndsWith('/') ? mount.ContainerPath : mount.ContainerPath + "/";
            if (!containerPath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = containerPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var hostPath = Path.Combine(mount.HostPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(hostPath)!);
            File.WriteAllBytes(hostPath, []);
            return;
        }
    }

    /// <summary>Register a deterministic outcome for an executable plus space-joined arguments.</summary>
    public void RegisterCommand(string commandLine, long exitCode, string standardOutput = "", string standardError = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        _scriptedCommands[commandLine] = new DockerExecutionOutcome
        {
            ExitCode = exitCode,
            StandardOutput = standardOutput,
            StandardError = standardError,
            StandardOutputTruncated = false,
            StandardErrorTruncated = false
        };
    }

    private static string BuildCommandKey(DockerExecutionRequest request)
    {
        return request.Arguments.Count == 0
            ? request.Executable
            : request.Executable + " " + string.Join(" ", request.Arguments);
    }


    // ---------------------------------------------------------------------------------------------------------
    // IContainerRuntime — the application-container surface, with a programmable lie behind every read-back field.
    // Each hook is something a real daemon cannot be asked to do on demand, and each one makes a fail-closed branch
    // of the layer above reachable. Defaults are conformant: with no hook installed this behaves like a daemon that
    // did exactly what it was told.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    ///     Rewrites what <see cref="InspectAsync" /> reports. The one hook that reaches every read-back check at once:
    ///     a daemon reporting a capability nobody asked for, a writable root filesystem, a device, or — the case the
    ///     loopback rule exists for — a published port bound to <c>0.0.0.0</c>.
    /// </summary>
    public Func<ContainerInspection, ContainerInspection>? InspectionMutator { get; set; }

    /// <summary>
    ///     The host port the daemon assigns for a container port, when it does not assign the one that was preferred.
    ///     Makes "read the resolved port back rather than assume it" a path a test can walk.
    /// </summary>
    public Func<int, int>? AssignedHostPorts { get; set; }

    /// <summary>
    ///     Health states handed out in order, one per inspect, so <c>starting → starting → healthy</c> and
    ///     <c>starting → unhealthy</c> are both reachable and the wait loop's timeout is testable without a clock.
    /// </summary>
    public Queue<ContainerHealthState> HealthSequence { get; } = new();

    /// <summary>When it returns a failure, <see cref="RunContainerAsync" /> throws it — a create refused after the network exists.</summary>
    public Func<ContainerSpecification, DockerRuntimeException?>? RunFailure { get; set; }

    /// <summary>What <see cref="StopContainerAsync" /> answers: false is "it was already stopped", which is not an error.</summary>
    public Func<string, bool>? StopOutcome { get; set; }

    /// <summary>When set, <see cref="PullImageAsync" /> throws it instead of pulling.</summary>
    public DockerRuntimeException? PullFailure { get; set; }

    /// <summary>Progress reports <see cref="PullImageAsync" /> replays, so relay is testable without a registry.</summary>
    public IReadOnlyList<ContainerPullProgress> PullProgressScript { get; set; } = [];

    /// <summary>When it returns a failure, <see cref="CreateNetworkAsync" /> throws it.</summary>
    public Func<string, DockerRuntimeException?>? NetworkCreateFailure { get; set; }

    /// <summary>When it returns a failure, <see cref="RemoveNetworkAsync" /> throws it — the partial-teardown path.</summary>
    public Func<string, DockerRuntimeException?>? NetworkRemovalFailure { get; set; }

    /// <summary>
    ///     Overrides the run state of a container: an exit code and an out-of-memory kill, which is what the
    ///     reconciler branches on and what a fake that only knew "started" could never produce.
    /// </summary>
    public Func<string, ContainerRunState>? ExitState { get; set; }

    /// <summary>
    ///     For a container whose command RUNS TO COMPLETION rather than serving: the exit code it finishes with the
    ///     instant it is started, or null to keep the container running like every other one.
    ///     <para>
    ///         A one-shot container is a shape this fake had no way to express — <see cref="StartContainerAsync" />
    ///         only ever made a container running, forever — so a caller that starts one and waits for it to exit
    ///         could never be tested against it. Keyed on the SPECIFICATION rather than on an id, because the caller
    ///         creates the container itself and a test never sees the id before the wait begins.
    ///     </para>
    /// </summary>
    public Func<ContainerSpecification, long?>? OneShotExitCode { get; set; }

    /// <summary>
    ///     Whether the write probe succeeds for a container and path. Default true; returning false is the only way
    ///     to reach the storage-failure branch without a daemon whose uid mapping can be arranged on demand.
    /// </summary>
    public Func<string, string, bool>? WritableProbeOutcome { get; set; }

    /// <summary>Every (container id, container path) the write probe was asked about, in order.</summary>
    public IReadOnlyList<(string ContainerId, string ContainerPath)> ProbedWritablePaths => [.. _probedPaths];

    /// <summary>
    ///     Mounts the daemon reports that nobody asked for — an image's own <c>VOLUME</c>. Reachable no other way
    ///     without building an image, and it is the mount an application policy has to reject.
    /// </summary>
    public Func<string, IReadOnlyList<ContainerMountView>>? AnonymousMounts { get; set; }

    /// <summary>Log lines <see cref="ReadLogsAsync" /> serves, oldest first.</summary>
    public IList<string> LogLines { get; } = [];

    /// <summary>Image references handed to <see cref="PullImageAsync" />, in order.</summary>
    public IReadOnlyList<string> PulledImages => [.. _pulledImages];

    /// <summary>Grace periods <see cref="StopContainerAsync" /> was given, keyed by container id.</summary>
    public IReadOnlyDictionary<string, TimeSpan> StoppedGracePeriods => _stoppedGracePeriods;

    /// <summary>Network specifications created through this client, in order.</summary>
    public IReadOnlyList<ContainerNetworkSpecification> CreatedNetworks => [.. _networks.Values];

    /// <summary>Network names removed through this client, in order.</summary>
    public IReadOnlyList<string> RemovedNetworks => [.. _removedNetworks];

    public Task<string> RunContainerAsync(ContainerSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        cancellationToken.ThrowIfCancellationRequested();

        // The same four refusals the production client makes, in the same order and before anything is recorded —
        // a fake that accepted what the daemon path refuses would let a test prove a guard that does not exist.
        if (specification.PublishedPorts.FirstOrDefault(publication =>
                !string.Equals(publication.HostIp, "127.0.0.1", StringComparison.Ordinal)) is { } offender)
        {
            throw new ArgumentException(
                $"Container port {offender.ContainerPort}/{offender.Protocol} asks to publish on host interface "
                + $"'{offender.HostIp}'. Application containers publish on 127.0.0.1 and on nothing else.",
                nameof(specification));
        }

        if (!specification.Image.Contains("@sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Image '{specification.Image}' is not digest-pinned.", nameof(specification));
        }

        if (specification.User is not null && string.IsNullOrWhiteSpace(specification.User))
        {
            throw new ArgumentException("A blank container user is refused; pass null for the image's default user.",
                nameof(specification));
        }

        if (specification.PublishedPorts
                         .GroupBy(publication => publication.ContainerPort.ToString(CultureInfo.InvariantCulture) + "/" + publication.Protocol,
                             StringComparer.Ordinal)
                         .FirstOrDefault(group => group.Skip(1).Any()) is { } duplicated)
        {
            throw new ArgumentException($"Container port {duplicated.Key} is published more than once.", nameof(specification));
        }

        if (RunFailure?.Invoke(specification) is { } failure)
        {
            throw failure;
        }

        // The three refusals a daemon makes on the state it holds rather than on the request. A fake that created a
        // container from an image it never pulled, on a network nobody created, under a name already taken would let
        // a pipeline prove an ordering it does not have: all three are 404/409 on the wire, which the real client
        // classifies through one mapping.
        if (!_images.ContainsKey(specification.Image))
        {
            throw Rejected("NotFound", $"No such image: {specification.Image}");
        }

        if (!IsBuiltInNetwork(specification.NetworkName) && !_networks.ContainsKey(specification.NetworkName))
        {
            throw Rejected("NotFound", $"No such network: {specification.NetworkName}");
        }

        if (_containers.Values.Any(existing => string.Equals(existing.ApplicationSpecification?.Name, specification.Name, StringComparison.Ordinal)))
        {
            throw Rejected("Conflict", $"The container name '{specification.Name}' is already in use");
        }

        var containerId = "fake-container-" + Interlocked.Increment(ref _containerCounter);
        _containers[containerId] = new ContainerRecord(specification);
        CreatedContainerIds = [.. CreatedContainerIds, containerId];
        return Task.FromResult(containerId);
    }

    /// <summary>
    ///     Records an image as already present in this daemon's local store, without a pull. The arrangement a test
    ///     makes when the pull is not what it is about; it does not appear in <see cref="PulledImages" />, because
    ///     nothing pulled it.
    /// </summary>
    public void SeedExistingImage(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        _images[imageReference] = true;
    }

    /// <summary>
    ///     Records a network as already present on this daemon, without a create. It does not appear in
    ///     <see cref="CreatedNetworks" />: a network this client did not create is one it did not create.
    /// </summary>
    public void SeedExistingNetwork(ContainerNetworkSpecification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        _networks[specification.Name] = specification;
        _networkIds[specification.Name] = "fake-network-" + Interlocked.Increment(ref _networkCounter);
    }

    /// <summary>
    ///     The networks every daemon ships with. A create naming one of them never 404s, which is what lets the
    ///     storage helper run on <c>none</c> without anything having created it.
    /// </summary>
    private static bool IsBuiltInNetwork(string networkName)
    {
        return networkName is "none" or "host" or "bridge";
    }

    /// <summary>
    ///     The shape the real client's <c>Classify</c> gives a daemon rejection: the status in the message and
    ///     <see cref="DockerDaemonPreflightStatus.ProbeFailed" /> on the exception, because an API rejection says
    ///     nothing about whether the daemon is usable.
    /// </summary>
    private DockerRuntimeException Rejected(string status, string detail)
    {
        return new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed,
            $"The Docker daemon at '{Endpoint.Display}' rejected the request with {status}. {detail}.");
    }

    public Task<ContainerInspection> InspectAsync(string containerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var record = GetRecord(containerId);
        var specification = record.ApplicationSpecification
                            ?? throw new DockerRuntimeException($"Container '{containerId}' was not created through the container runtime surface.");

        var mounts = new List<ContainerMountView>(specification.Mounts.Select(mount => new ContainerMountView
        {
            Type = "bind",
            Source = mount.HostPath,
            Destination = mount.ContainerPath,
            ReadOnly = mount.ReadOnly
        }));

        if (AnonymousMounts?.Invoke(containerId) is { } extra)
        {
            mounts.AddRange(extra);
        }

        var conformant = new ContainerInspection
        {
            ContainerId = containerId,
            Name = specification.Name,
            Image = specification.Image,
            Labels = specification.Labels,
            State = RunStateOf(containerId, record, specification),
            // Requested from create onwards; published only once started. A fake that filled both at create would
            // pass a two-pass port verification in tests and fail against a real daemon.
            RequestedPortBindings = specification.PublishedPorts,
            PublishedPorts = record.Started ? record.PublishedPorts ?? [] : [],
            User = specification.User ?? string.Empty,
            NetworkMode = specification.NetworkName,
            Privileged = false,
            ReadOnlyRootFilesystem = specification.ReadOnlyRootFilesystem,
            CapabilitiesDropped = specification.CapabilitiesToDrop,
            CapabilitiesAdded = specification.CapabilitiesToAdd,
            SecurityOptions = specification.SecurityOptions,
            Mounts = mounts,
            DeviceCount = 0,
            PidMode = string.Empty,
            IpcMode = "private",
            UtsMode = string.Empty,
            MemoryBytes = specification.MemoryBytes,
            NanoCpus = specification.NanoCpus,
            PidsLimit = specification.PidsLimit,
            RestartMode = specification.RestartMode,
            ExtraHosts = specification.ExtraHosts
        };

        return Task.FromResult(InspectionMutator is null ? conformant : InspectionMutator(conformant));
    }

    public Task<bool> StopContainerAsync(string containerId, TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        cancellationToken.ThrowIfCancellationRequested();

        _stoppedGracePeriods[containerId] = gracePeriod;

        if (StopOutcome is { } outcome)
        {
            var stopped = outcome(containerId);
            if (stopped && _containers.TryGetValue(containerId, out var scripted))
            {
                scripted.Started = false;
            }

            return Task.FromResult(stopped);
        }

        if (!_containers.TryGetValue(containerId, out var record) || !record.Started)
        {
            return Task.FromResult(false);
        }

        record.Started = false;
        return Task.FromResult(true);
    }

    public Task<bool> ImageExistsAsync(string imageReference, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_images.ContainsKey(imageReference));
    }

    public Task PullImageAsync(string imageReference,
        IProgress<ContainerPullProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        cancellationToken.ThrowIfCancellationRequested();

        if (!imageReference.Contains("@sha256:", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Image '{imageReference}' is not digest-pinned.", nameof(imageReference));
        }

        if (PullFailure is { } failure)
        {
            return Task.FromException(failure);
        }

        foreach (var report in PullProgressScript)
        {
            progress?.Report(report);
        }

        _pulledImages.Enqueue(imageReference);
        _images[imageReference] = true;
        return Task.CompletedTask;
    }

    public Task<string> CreateNetworkAsync(ContainerNetworkSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (string.IsNullOrWhiteSpace(specification.Name))
        {
            throw new ArgumentException("A container network specification must carry a name.", nameof(specification));
        }

        // The same refusal the daemon path makes, for the same reason: with no labels the ownership check below has
        // nothing to compare and a foreign network holding the name would be reused.
        if (specification.Labels.Count == 0)
        {
            throw new ArgumentException(
                $"The container network '{specification.Name}' carries no labels. At least one ownership label is required.",
                nameof(specification));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (NetworkCreateFailure?.Invoke(specification.Name) is { } failure)
        {
            throw failure;
        }

        if (_networks.TryGetValue(specification.Name, out var existing))
        {
            // The same ownership rule the daemon path applies: a name conflict is reuse only when every label of the
            // specification is present and equal on the existing network.
            var owned = specification.Labels.All(label => existing.Labels.TryGetValue(label.Key, out var actual)
                                                          && string.Equals(actual, label.Value, StringComparison.Ordinal));

            return owned
                ? Task.FromResult(_networkIds[specification.Name])
                : throw new ContainerPolicyException(ContainerPolicyException.ForeignNetworkReason,
                    $"The network name '{specification.Name}' is in use by a foreign container network. It was not "
                    + "created by this application instance, so it is not reused.");
        }

        var networkId = "fake-network-" + Interlocked.Increment(ref _networkCounter);
        _networks[specification.Name] = specification;
        _networkIds[specification.Name] = networkId;
        return Task.FromResult(networkId);
    }

    public Task RemoveNetworkAsync(string networkNameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkNameOrId);
        cancellationToken.ThrowIfCancellationRequested();

        if (NetworkRemovalFailure?.Invoke(networkNameOrId) is { } failure)
        {
            throw failure;
        }

        // Idempotent by contract: a network that is already gone is not an error, which is what lets a teardown run
        // twice without having to remember how far the first attempt got.
        var name = _networkIds.FirstOrDefault(pair => string.Equals(pair.Value, networkNameOrId, StringComparison.Ordinal)).Key
                   ?? networkNameOrId;
        _networks.TryRemove(name, out _);
        _networkIds.TryRemove(name, out _);
        _removedNetworks.Enqueue(networkNameOrId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNetworksAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        cancellationToken.ThrowIfCancellationRequested();

        if (labels.Count == 0)
        {
            throw new ArgumentException("Listing networks without a label filter is refused: the result is used to remove networks.",
                nameof(labels));
        }

        IReadOnlyList<string> matches =
        [
            .. _networks
               .Where(entry => labels.All(filter => entry.Value.Labels.TryGetValue(filter.Key, out var value)
                                                    && string.Equals(value, filter.Value, StringComparison.Ordinal)))
               .Select(entry => _networkIds[entry.Key])
               .Order(StringComparer.Ordinal)
        ];

        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<ContainerSummary>> ListContainersDetailedAsync(IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        cancellationToken.ThrowIfCancellationRequested();

        if (labels.Count == 0)
        {
            throw new ArgumentException("Listing containers without a label filter is refused: the result drives lifecycle decisions.",
                nameof(labels));
        }

        IReadOnlyList<ContainerSummary> matches =
        [
            .. _containers
               .Where(entry => labels.All(filter => entry.Value.Labels.TryGetValue(filter.Key, out var value)
                                                    && string.Equals(value, filter.Value, StringComparison.Ordinal)))
               .OrderBy(entry => entry.Key, StringComparer.Ordinal)
               .Select(entry => ToSummary(entry.Key, entry.Value))
        ];

        return Task.FromResult(matches);
    }

    public Task<ContainerLogSnapshot> ReadLogsAsync(string containerId,
        ContainerLogRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        GetRecord(containerId);

        var tail = LogLines.Count <= request.TailLines ? LogLines : [.. LogLines.Skip(LogLines.Count - request.TailLines)];
        var text = tail.Count == 0 ? string.Empty : string.Join('\n', tail) + "\n";
        var truncated = false;

        if (Encoding.UTF8.GetByteCount(text) > request.MaxBytes)
        {
            text = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text), 0, request.MaxBytes);
            truncated = true;
        }

        return Task.FromResult(new ContainerLogSnapshot
        {
            Text = text,
            Truncated = truncated,
            LineCount = text.AsSpan().Count('\n')
        });
    }

    public Task<bool> ProbeWritablePathAsync(string containerId, string containerPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        cancellationToken.ThrowIfCancellationRequested();

        _probedPaths.Enqueue((containerId, containerPath));

        if (!_containers.ContainsKey(containerId))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(WritableProbeOutcome?.Invoke(containerId, containerPath) ?? true);
    }

    private ContainerSummary ToSummary(string containerId, ContainerRecord record)
    {
        var exit = ExitState?.Invoke(containerId);
        var running = exit?.Running ?? (record.Started && record.FinishedWith is null);

        // "created" is the daemon's own word for a container that has never been started, and the reconciler and the
        // observer render this string verbatim. Collapsing it into "exited" would report a container the engine
        // created and never started as one that ran and died.
        var state = "created";
        if (running)
        {
            state = "running";
        }
        else if (record.EverStarted)
        {
            state = "exited";
        }

        // Null is "the daemon did not say", and is never read as 0 — that would report a crash as a clean exit. A
        // one-shot that has finished DID say, through the same field InspectAsync reports it from: the two reads of
        // one container must not disagree about whether it left an exit code behind.
        int? exitCode = null;
        if (exit is not null)
        {
            exitCode = (int)exit.ExitCode;
        }
        else if (record.FinishedWith is { } finished)
        {
            exitCode = (int)finished;
        }

        return new ContainerSummary
        {
            Id = containerId,
            Labels = record.Labels,
            State = state,
            ExitCode = exitCode
        };
    }

    private ContainerRunState RunStateOf(string containerId, ContainerRecord record, ContainerSpecification specification)
    {
        if (ExitState?.Invoke(containerId) is { } scripted)
        {
            return scripted;
        }

        if (record.FinishedWith is { } finished)
        {
            // A one-shot container: started, and already over. The health sequence is deliberately not consumed —
            // a container that runs a command to completion never reports one.
            return new ContainerRunState
            {
                Running = false,
                Status = "exited",
                ExitCode = finished,
                OutOfMemoryKilled = false,
                Health = ContainerHealthState.None,
                StartedAtUtc = null,
                FinishedAtUtc = null
            };
        }

        var declaredHealth = specification.Healthcheck is null ? ContainerHealthState.None : ContainerHealthState.Healthy;
        var health = HealthSequence.Count > 0 ? HealthSequence.Dequeue() : declaredHealth;

        return new ContainerRunState
        {
            Running = record.Started,
            Status = record.Started ? "running" : "created",
            ExitCode = 0,
            OutOfMemoryKilled = false,
            Health = health,
            StartedAtUtc = null,
            FinishedAtUtc = null
        };
    }

    private IReadOnlyList<ContainerPublishedPort> PublishedPortsOf(ContainerSpecification specification)
    {
        return
        [
            .. specification.PublishedPorts.Select(publication => new ContainerPublishedPort
            {
                ContainerPort = publication.ContainerPort,
                Protocol = publication.Protocol,
                HostIp = publication.HostIp,
                HostPort = AssignedHostPorts?.Invoke(publication.ContainerPort)
                           ?? publication.HostPort
                           ?? 30000 + Interlocked.Increment(ref _hostPortCounter)
            })
        ];
    }

    private ContainerRecord GetRecord(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        return _containers.TryGetValue(containerId, out var record)
            ? record
            : throw new DockerRuntimeException($"No fake container '{containerId}' exists.");
    }

    /// <summary>
    ///     One "created" container. It holds whichever specification created it — Development Mode's or the
    ///     application runtime's — and never both, because the two surfaces create different things and a record that
    ///     pretended otherwise would let a test inspect a container through the surface that did not make it.
    /// </summary>
    private sealed class ContainerRecord
    {
        public ContainerRecord(DockerContainerSpecification specification)
        {
            Specification = specification;
            Labels = specification.Labels;
        }

        public ContainerRecord(ContainerSpecification specification)
        {
            ApplicationSpecification = specification;
            Labels = specification.Labels;
        }

        public DockerContainerSpecification? Specification { get; }

        public ContainerSpecification? ApplicationSpecification { get; }

        /// <summary>The labels either specification carried, so one label filter serves both surfaces.</summary>
        public IReadOnlyDictionary<string, string> Labels { get; }

        public bool Started { get; set; }

        /// <summary>
        ///     Whether this container was ever started. <see cref="Started" /> alone cannot say: a stop clears it, and
        ///     a container that ran and was stopped is <c>exited</c> to the daemon while one that never ran is
        ///     <c>created</c>.
        /// </summary>
        public bool EverStarted { get; set; }

        /// <summary>The exit code a one-shot container's command finished with, or null while it is still running.</summary>
        public long? FinishedWith { get; set; }

        /// <summary>The ports assigned when this container was started, or null before that. Assigned once.</summary>
        public IReadOnlyList<ContainerPublishedPort>? PublishedPorts { get; set; }
    }
}
