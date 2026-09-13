namespace XE_Local_AI_Engine.Client.Services.Containers.Implementation;

using System.Globalization;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     The one door application containers reach a Docker daemon through: read the operator's selection, resolve an
///     endpoint, refuse a remote one outright, probe, and turn the evidence into a status an operator can act on.
///     <para>
///         It writes its own prose. The shared probe deliberately produces none, because ADR 0004 makes Development
///         Mode's preflight messages the entire experience of a missing daemon for that feature, and a user whose
///         installed application will not start needs different words for the identical status.
///     </para>
/// </summary>
internal sealed class ContainerRuntimeResolver : IContainerRuntimeResolver, IDisposable
{
    /// <summary>The only provider in V1. Both <see cref="ContainerRuntimeSelection" /> values resolve to it.</summary>
    private const string DockerProvider = "docker";

    private readonly IDockerDaemonAttestationStore _attestationStore;
    private readonly Func<string?, DockerDaemonEndpoint> _endpointResolver;
    private readonly IContainerRuntimeFactory _factory;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly ILogger<ContainerRuntimeResolver> _logger;
    private readonly IOptionsMonitor<ContainerRuntimeOptions> _options;
    private readonly INodeSettingsStore _settingsStore;
    private readonly TimeProvider _timeProvider;

    private ContainerRuntimeResolution? _cached;
    private DateTimeOffset _cachedAtUtc;
    private DockerDaemonEndpoint? _cachedEndpoint;
    private ContainerRuntimeSelection _cachedSelection;

    public ContainerRuntimeResolver(INodeSettingsStore settingsStore,
        IOptionsMonitor<ContainerRuntimeOptions> options,
        IContainerRuntimeFactory factory,
        IDockerDaemonAttestationStore attestationStore,
        TimeProvider timeProvider,
        ILogger<ContainerRuntimeResolver> logger)
        : this(settingsStore, options, factory, attestationStore, timeProvider, logger, endpointResolver: null)
    {
    }

    /// <summary>
    ///     Test seam. <paramref name="endpointResolver" /> replaces discovery so that the endpoint order — and in
    ///     particular a <c>DOCKER_HOST</c> naming a remote daemon — can be exercised without mutating the test
    ///     process's environment, which every other test in the run shares. Null means production discovery.
    /// </summary>
    internal ContainerRuntimeResolver(INodeSettingsStore settingsStore,
        IOptionsMonitor<ContainerRuntimeOptions> options,
        IContainerRuntimeFactory factory,
        IDockerDaemonAttestationStore attestationStore,
        TimeProvider timeProvider,
        ILogger<ContainerRuntimeResolver> logger,
        Func<string?, DockerDaemonEndpoint>? endpointResolver)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _attestationStore = attestationStore ?? throw new ArgumentNullException(nameof(attestationStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _endpointResolver = endpointResolver ?? DockerDaemonEndpointResolver.Resolve;
    }

    /// <summary>Releases the resolution gate. The container disposes this singleton at shutdown.</summary>
    public void Dispose()
    {
        _gate.Dispose();
    }

    public async Task<ContainerRuntimeResolution> ResolveAsync(ContainerRuntimeSelection? instanceOverride = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var (resolution, _) = await ResolveWithEndpointAsync(instanceOverride, forceRefresh, confirmingDaemonId: null, cancellationToken)
            .ConfigureAwait(false);
        return resolution;
    }

    public async Task<ContainerRuntimeResolution> ConfirmDaemonIdentityAsync(string expectedDaemonId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDaemonId);

        // Always probes, cache or no cache: a confirmation applied to an observation made a moment ago would approve
        // whichever daemon is there now rather than the one the operator was shown.
        var (resolution, _) = await ResolveWithEndpointAsync(instanceOverride: null,
                forceRefresh: true,
                expectedDaemonId,
                cancellationToken)
            .ConfigureAwait(false);
        return resolution;
    }

    public async Task<IContainerRuntime> CreateRuntimeAsync(ContainerRuntimeSelection? instanceOverride = null,
        CancellationToken cancellationToken = default)
    {
        var (resolution, endpoint) = await ResolveWithEndpointAsync(instanceOverride,
                forceRefresh: false,
                confirmingDaemonId: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (!resolution.Ready)
        {
            // No degraded mode: a container created against a daemon that failed its preflight is one whose
            // confinement was never established, which is worse than the application not starting.
            throw new ContainerRuntimeUnavailableException(resolution);
        }

        return _factory.CreateRuntime(endpoint);
    }

    /// <summary>
    ///     The preflight-to-runtime status mapping, exhaustive by construction.
    ///     <para>
    ///         The catch-all only throws: C# demands one for an out-of-range cast (CS8524) and would reject the switch
    ///         outright if a named status were missing, so a status added to the preflight enum cannot silently
    ///         inherit another's meaning here. <c>ContainerRuntimeResolverTests.EveryPreflightStatus_MapsToItsRuntimeStatus</c>
    ///         enumerates the enum, so an added status is a red test rather than a throw an operator meets first.
    ///     </para>
    /// </summary>
    internal static ContainerRuntimeStatus ToRuntimeStatus(DockerDaemonPreflightStatus status)
    {
        return status switch
        {
            DockerDaemonPreflightStatus.Ready => ContainerRuntimeStatus.Ready,
            DockerDaemonPreflightStatus.DaemonUnreachable => ContainerRuntimeStatus.DaemonUnreachable,
            DockerDaemonPreflightStatus.PermissionDenied => ContainerRuntimeStatus.PermissionDenied,
            DockerDaemonPreflightStatus.ApiVersionTooOld => ContainerRuntimeStatus.ApiVersionTooOld,
            DockerDaemonPreflightStatus.DaemonIdentityChanged => ContainerRuntimeStatus.DaemonIdentityChanged,
            DockerDaemonPreflightStatus.NotConfigured => ContainerRuntimeStatus.NotConfigured,
            DockerDaemonPreflightStatus.ProbeFailed => ContainerRuntimeStatus.ProbeFailed,
            _ => throw new InvalidOperationException($"Unhandled daemon preflight status '{status}'.")
        };
    }

    private async Task<(ContainerRuntimeResolution Resolution, DockerDaemonEndpoint Endpoint)> ResolveWithEndpointAsync(ContainerRuntimeSelection? instanceOverride,
        bool forceRefresh,
        string? confirmingDaemonId,
        CancellationToken cancellationToken)
    {
        var selection = await ResolveSelectionAsync(instanceOverride, cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = _options.CurrentValue;

            if (!forceRefresh
                && _cached is not null
                && _cachedEndpoint is not null
                && _cachedSelection == selection
                && _timeProvider.GetUtcNow() - _cachedAtUtc < TimeSpan.FromSeconds(options.ResolutionCacheSeconds))
            {
                return (_cached, _cachedEndpoint);
            }

            var (resolution, endpoint) = await ProbeAsync(options, confirmingDaemonId, cancellationToken).ConfigureAwait(false);

            _cached = resolution;
            _cachedEndpoint = endpoint;
            _cachedSelection = selection;
            _cachedAtUtc = _timeProvider.GetUtcNow();

            return (resolution, endpoint);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     The effective selection: an instance's override, else the stored node setting, else <c>Auto</c>.
    ///     <para>
    ///         An override skips the settings read entirely rather than reading and discarding it — outranking a value
    ///         and then loading it anyway is how a resolver acquires a persistence dependency on a path that has no
    ///         reason to have one. An unparsable stored value falls back to <c>Auto</c> rather than failing: a
    ///         hand-edited settings file must not be able to take the runtime layer down.
    ///     </para>
    /// </summary>
    private async Task<ContainerRuntimeSelection> ResolveSelectionAsync(ContainerRuntimeSelection? instanceOverride,
        CancellationToken cancellationToken)
    {
        if (instanceOverride is { } explicitSelection)
        {
            return explicitSelection;
        }

        var stored = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!ContainerRuntimeSelectionParser.TryParse(stored.ContainerRuntimeSelection, out var selection)
            && !string.IsNullOrWhiteSpace(stored.ContainerRuntimeSelection))
        {
            _logger.LogWarning("Stored container runtime selection '{Selection}' is not recognised; using the default.",
                stored.ContainerRuntimeSelection);
        }

        return selection;
    }

    private async Task<(ContainerRuntimeResolution Resolution, DockerDaemonEndpoint Endpoint)> ProbeAsync(ContainerRuntimeOptions options,
        string? confirmingDaemonId,
        CancellationToken cancellationToken)
    {
        var endpoint = _endpointResolver(options.DaemonEndpoint);

        // First of all, and without repeating the value back. User information, a query string and a fragment are all
        // operator-supplied, none of them addresses a Docker daemon, and any of the three is somewhere a token fits —
        // a DOCKER_HOST of tcp://host:2375/?token=… is a value an operator can set. The refusal below would otherwise
        // print it into a log line, a resolution record, an exception message and the 503 body those become, so it
        // names only which component is at fault. The endpoint itself renders redacted (DockerDaemonEndpoint.Display).
        if (endpoint.DisclosingComponent is { } disclosing)
        {
            return (Reported(Refused(endpoint,
                "The Docker daemon endpoint " + DescribeSource(endpoint.Source) + " carries " + disclosing + " in its URI. "
                + "It is refused without being repeated back, because a secret that has reached a log file or an "
                + "error message is no longer a secret. Remove " + disclosing + " from the endpoint, and treat "
                + "whatever was in it as disclosed.")), endpoint);
        }

        // Before any client is constructed and before anything is transmitted. Development Mode accepts a remote
        // daemon today and its messages must not change, so the refusal lives here rather than in the shared probe.
        if (!IsLocalTransport(endpoint))
        {
            return (Reported(Refused(endpoint,
                $"Remote daemons are not supported in this version. The endpoint {endpoint.Display} "
                + $"({DescribeSource(endpoint.Source)}) uses the '{endpoint.Uri.Scheme}' transport; applications run only "
                + "against a local Docker socket or named pipe, because a container on another host cannot be given this "
                + "node's application directories. Point the engine at a local daemon, or clear the setting that names "
                + "this one.")), endpoint);
        }

        var outcome = await DockerDaemonProbe.RunAsync(new DockerDaemonProbeRequest
            {
                // The endpoint this resolver already settled on, handed over as itself rather than restated as a
                // string: the probe's own discovery cannot land somewhere else between the transport check above and
                // the wire call below, AND the source that named it survives into the pin the probe writes. Restating
                // it would make every resolution look like engine configuration to whoever reads that pin next.
                ResolvedEndpoint = endpoint,
                ConfiguredEndpoint = options.DaemonEndpoint,
                MinimumApiVersion = options.MinimumApiVersion,

                // Application containers are created under the engine seccomp profile, which a daemon without seccomp
                // support accepts and silently does not apply. Same claim as Development Mode, same refusal.
                RequireSeccompSupport = true,
                ConfirmingDaemonId = confirmingDaemonId
            },
            _factory.CreateRuntime,
            _attestationStore,
            _timeProvider,
            _logger,
            cancellationToken).ConfigureAwait(false);

        return (Reported(Describe(outcome, endpoint, options)), endpoint);
    }

    /// <summary>
    ///     One line per unavailable resolution, mirroring Development Mode's transport-failure line.
    ///     <para>
    ///         Without it a node whose installed applications will not start says nothing at all in its log until
    ///         somebody opens the UI, and the operator prose this class writes so carefully reaches only a caller that
    ///         asked. Information rather than warning: no daemon is the ordinary, correct state of a node that does
    ///         not run application containers. It logs the endpoint and the status only — the same two facts the
    ///         preflight logs, and nothing the resolution's own message would not already say.
    ///     </para>
    /// </summary>
    private ContainerRuntimeResolution Reported(ContainerRuntimeResolution resolution)
    {
        if (!resolution.Ready)
        {
            _logger.LogInformation("Container runtime is unavailable at {Endpoint}: {Status}.",
                resolution.Daemon.Endpoint,
                resolution.Status);
        }

        return resolution;
    }

    private static ContainerRuntimeResolution Describe(DockerDaemonProbeOutcome outcome,
        DockerDaemonEndpoint endpoint,
        ContainerRuntimeOptions options)
    {
        var status = ToRuntimeStatus(outcome.Status);
        var daemon = Summarize(outcome, endpoint);

        // A Windows-container daemon answers every question this probe asks and then means something different by
        // every answer: a bind mount, a loopback published port and a Linux image reference are not the same objects
        // there. Refused after the probe rather than before it, because only the daemon can say which it is.
        if (status == ContainerRuntimeStatus.Ready
            && !string.Equals(outcome.ObservedDaemon?.OperatingSystem, "linux", StringComparison.OrdinalIgnoreCase))
        {
            var reportedOperatingSystem = string.IsNullOrWhiteSpace(outcome.ObservedDaemon?.OperatingSystem)
                ? "an unreported operating system"
                : outcome.ObservedDaemon.OperatingSystem;

            return new ContainerRuntimeResolution
            {
                Provider = DockerProvider,
                Status = ContainerRuntimeStatus.ProbeFailed,
                Capabilities = ContainerRuntimeCapabilities.None,
                Daemon = daemon,
                Message = $"The Docker daemon at {endpoint.Display} runs {reportedOperatingSystem} containers, and "
                          + "applications are Linux containers. Switch the daemon to Linux containers, or point the "
                          + "engine at one that already runs them."
            };
        }

        return new ContainerRuntimeResolution
        {
            Provider = DockerProvider,
            Status = status,
            Capabilities = status == ContainerRuntimeStatus.Ready
                ? ContainerRuntimeCapabilities.DockerReady
                : ContainerRuntimeCapabilities.None,
            Daemon = daemon,
            Message = Describe(outcome, endpoint, options, daemon)
        };
    }

    private static string Describe(DockerDaemonProbeOutcome outcome,
        DockerDaemonEndpoint endpoint,
        ContainerRuntimeOptions options,
        ContainerDaemonSummary daemon)
    {
        var where = $"{endpoint.Display} ({DescribeSource(endpoint.Source)})";

        return outcome.Reason switch
        {
            DockerDaemonProbeReason.Ready =>
                $"Docker is ready at {where}. Engine {daemon.ServerVersion}, API {daemon.ApiVersion}"
                + (daemon.IsRootless ? ", rootless." : "."),

            DockerDaemonProbeReason.TransportFailure => DescribeTransportFailure(outcome, where),

            DockerDaemonProbeReason.ApiVersionTooOld =>
                $"The Docker daemon at {where} serves API {daemon.ApiVersion}, and applications need at least "
                + $"{outcome.RequiredApiVersion ?? options.MinimumApiVersion}. Upgrade Docker Engine, or point the "
                + "engine at a newer daemon.",

            DockerDaemonProbeReason.SeccompUnsupported =>
                $"The Docker daemon at {where} does not report seccomp support. Applications are created under a "
                + "seccomp profile that such a daemon accepts and then silently does not apply, so they stay stopped "
                + "rather than running with confinement that cannot be proven.",

            DockerDaemonProbeReason.IdentityChanged =>
                $"The daemon answering at {where} is not the one this node approved"
                + FormatPinnedSuffix(daemon)
                + ". Applications stay stopped until you confirm that this is the daemon you mean: their data "
                + "directories and published ports were arranged against the previous one.",

            DockerDaemonProbeReason.ConfirmationRaced =>
                $"The daemon at {where} changed again while you were confirming it, so nothing was approved. "
                + "Re-read the daemon shown now and confirm that one.",

            _ => throw new InvalidOperationException($"Unhandled daemon probe reason '{outcome.Reason}'.")
        };
    }

    private static string DescribeTransportFailure(DockerDaemonProbeOutcome outcome, string where)
    {
        var failure = outcome.ProbeFailure;

        return failure?.Status switch
        {
            DockerDaemonPreflightStatus.DaemonUnreachable =>
                $"Applications need a running Docker daemon and nothing answered at {where}. Start Docker, or set "
                + "DOCKER_HOST to the socket you want this node to use. Installed applications stay stopped until one "
                + "answers; nothing is run outside a container in its place.",

            DockerDaemonPreflightStatus.PermissionDenied =>
                $"A Docker daemon answered at {where} but this node is not permitted to use it. The account running "
                + "XE-Local-AI-Engine needs read and write access to that socket. Be aware of what that grants: on "
                + "Linux, access to the Docker socket is equivalent to root on this machine.",

            _ =>
                $"The container-runtime check against {where} did not complete: {failure?.Message} Applications stay "
                + "stopped until it does."
        };
    }

    private static string FormatPinnedSuffix(ContainerDaemonSummary daemon)
    {
        if (daemon.PinnedDaemonConfirmedAtUtc is not { } confirmedAt)
        {
            return string.Empty;
        }

        return " on " + confirmedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    private static ContainerDaemonSummary Summarize(DockerDaemonProbeOutcome outcome, DockerDaemonEndpoint endpoint)
    {
        return new ContainerDaemonSummary
        {
            Endpoint = endpoint.Display,
            EndpointSource = endpoint.Source,
            DaemonId = outcome.ObservedDaemon?.DaemonId,
            ServerVersion = outcome.ObservedDaemon?.ServerVersion,
            ApiVersion = outcome.ObservedDaemon?.ApiVersion,
            IsRootless = outcome.ObservedDaemon?.IsRootless ?? false,
            PinnedDaemonId = outcome.PinnedDaemon?.DaemonId,
            PinnedDaemonConfirmedAtUtc = outcome.PinnedDaemon?.ConfirmedAtUtc
        };
    }

    private static ContainerRuntimeResolution Refused(DockerDaemonEndpoint endpoint, string message)
    {
        return new ContainerRuntimeResolution
        {
            Provider = DockerProvider,
            Status = ContainerRuntimeStatus.ProbeFailed,
            Capabilities = ContainerRuntimeCapabilities.None,
            Message = message,
            Daemon = new ContainerDaemonSummary
            {
                Endpoint = endpoint.Display,
                EndpointSource = endpoint.Source
            }
        };
    }

    private static bool IsLocalTransport(DockerDaemonEndpoint endpoint)
    {
        return endpoint.Uri.Scheme.Equals("unix", StringComparison.OrdinalIgnoreCase)
               || endpoint.Uri.Scheme.Equals("npipe", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeSource(DockerDaemonEndpointSource source)
    {
        return source switch
        {
            DockerDaemonEndpointSource.Configuration => "from engine configuration",
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable => "from the DOCKER_HOST environment variable",
            DockerDaemonEndpointSource.DefaultUnixSocket => "the conventional system socket",
            DockerDaemonEndpointSource.UserRuntimeUnixSocket => "a per-user socket, the layout a rootless install produces",
            DockerDaemonEndpointSource.WindowsNamedPipe => "the conventional Windows named pipe",
            _ => throw new InvalidOperationException($"Unhandled daemon endpoint source '{source}'.")
        };
    }
}
