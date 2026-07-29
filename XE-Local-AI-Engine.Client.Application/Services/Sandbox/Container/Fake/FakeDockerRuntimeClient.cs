namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;

using System.Collections.Concurrent;

/// <summary>
///     A deterministic in-memory <see cref="IDockerRuntimeClient" /> for unit coverage of everything above the wire:
///     endpoint classification, D10 attestation, and — the reason it exists — the §3.8 fail-closed read-back.
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
public sealed class FakeDockerRuntimeClient : IDockerRuntimeClient
{
    private readonly ConcurrentDictionary<string, ContainerRecord> _containers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DockerExecutionOutcome> _scriptedCommands = new(StringComparer.Ordinal);
    private int _containerCounter;

    public FakeDockerRuntimeClient(DockerDaemonEndpoint endpoint, DockerDaemonIdentity? identity = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Identity = identity ?? new DockerDaemonIdentity("fake-daemon", "29.6.1", "1.55", "1.40", "linux", endpoint);
    }

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
        GetRecord(containerId).Started = true;
        return Task.CompletedTask;
    }

    public Task<DockerContainerSettings> InspectContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var specification = GetRecord(containerId).Specification;
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

    public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        GetRecord(containerId);

        var key = BuildCommandKey(request);
        return Task.FromResult(_scriptedCommands.TryGetValue(key, out var scripted)
            ? scripted
            : new DockerExecutionOutcome
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                StandardError = string.Empty,
                StandardOutputTruncated = false,
                StandardErrorTruncated = false
            });
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

    private ContainerRecord GetRecord(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        return _containers.TryGetValue(containerId, out var record)
            ? record
            : throw new DockerRuntimeException($"No fake container '{containerId}' exists.");
    }

    private sealed class ContainerRecord
    {
        public ContainerRecord(DockerContainerSpecification specification)
        {
            Specification = specification;
        }

        public DockerContainerSpecification Specification { get; }

        public bool Started { get; set; }
    }
}
