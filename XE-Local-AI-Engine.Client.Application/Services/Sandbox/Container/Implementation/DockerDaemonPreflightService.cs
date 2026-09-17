namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

using System.Globalization;
using Microsoft.Extensions.Options;

/// <summary>
///     The container-runtime preflight, and the home of every message an operator reads when Development Mode cannot
///     run.
///     <para>
///         ADR 0004 accepts that a user without a container runtime gets no Development Mode at all rather than a
///         degraded one, and rules out an unisolated fallback so that the product's isolation posture never depends on
///         what happens to be installed. The consequence is that these messages <em>are</em> the feature for that
///         user: they are the only thing standing between "Development Mode requires something you do not have" and
///         "Development Mode is broken". So each case names what was looked for, where, what was found, and the one
///         action that changes the answer.
///     </para>
///     <para>
///         Deliberately absent from all of them: any suggestion that rootless Docker is required or supplied. On Linux
///         access to the Docker socket is root-equivalent; ADR 0004 documents that rather than mitigating it, and
///         rootless Docker is the user's own option which this product neither depends on nor claims.
///     </para>
/// </summary>
internal sealed class DockerDaemonPreflightService : IDockerDaemonPreflightService
{
    private readonly IDockerDaemonAttestationStore _attestationStore;
    private readonly IDockerRuntimeClientFactory _clientFactory;
    private readonly ILogger<DockerDaemonPreflightService> _logger;
    private readonly IOptionsMonitor<ContainerSandboxOptions> _options;
    private readonly TimeProvider _timeProvider;

    public DockerDaemonPreflightService(IOptionsMonitor<ContainerSandboxOptions> options,
        IDockerRuntimeClientFactory clientFactory,
        IDockerDaemonAttestationStore attestationStore,
        TimeProvider timeProvider,
        ILogger<DockerDaemonPreflightService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _attestationStore = attestationStore ?? throw new ArgumentNullException(nameof(attestationStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DockerDaemonPreflight> InspectAsync(CancellationToken cancellationToken = default)
    {
        return await EvaluateAsync(confirmingDaemonId: null, cancellationToken);
    }

    public async Task<DockerDaemonPreflight> ConfirmAsync(string expectedDaemonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDaemonId);
        return await EvaluateAsync(expectedDaemonId, cancellationToken);
    }

    private async Task<DockerDaemonPreflight> EvaluateAsync(string? confirmingDaemonId, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(options.Image))
        {
            return new DockerDaemonPreflight
            {
                Status = DockerDaemonPreflightStatus.NotConfigured,
                Message = "Development Mode has no approved container image configured, so it cannot create a sandbox even "
                          + $"with a working container runtime. Set '{ContainerSandboxOptions.SectionName}:Image' to a "
                          + "digest-pinned image reference (one containing '@sha256:') that carries git, find, grep and the "
                          + "toolchain your repositories build with. A tag is not accepted: a tag names whatever the registry "
                          + "last pushed, not the bytes you approved."
            };
        }

        // The engine's own asset, checked before any daemon is contacted: the specification cannot be built without
        // it, so a broken build would otherwise surface as a failed container create rather than as a preflight that
        // names the cause. Reading it is a one-off — the profile is cached after the first call.
        try
        {
            _ = DockerSeccompProfile.SecurityOption;
        }
        catch (DockerRuntimeException exception)
        {
            _logger.LogError(exception, "Development Mode cannot read its embedded seccomp profile.");

            return new DockerDaemonPreflight
            {
                Status = DockerDaemonPreflightStatus.NotConfigured,
                Message = "Development Mode ships a seccomp profile that every sandbox container is created under, and this build "
                          + $"cannot read it: {exception.Message} Without it a container's system-call confinement cannot be "
                          + "established — a container created with no seccomp option reads back identically to one on a daemon with "
                          + "seccomp switched off — so Development Mode stays unavailable rather than running your code under "
                          + "confinement it cannot prove. Reinstall XE-Local-AI-Engine; this is a packaging fault, not a setting."
            };
        }

        // Resolved here rather than left to the probe so that the endpoint can be refused before a client is
        // constructed. User information, a query string and a fragment are all operator-supplied, none of them
        // addresses a Docker daemon, and any of the three is somewhere a token fits — a DOCKER_HOST of
        // tcp://host:2375/?token=… is a value an operator can set. Handing the resolved endpoint on rather than
        // restating it as a string keeps the source that named it, which is what the pin records. The probe would
        // resolve exactly this endpoint from the same configured value.
        var endpoint = DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint);
        if (endpoint.DisclosingComponent is { } disclosing)
        {
            return new DockerDaemonPreflight
            {
                Status = DockerDaemonPreflightStatus.NotConfigured,
                Message = $"The container runtime endpoint found via {Describe(endpoint.Source)} carries {disclosing}, and "
                          + "Development Mode refuses it without repeating the value back: a secret that has reached a log file "
                          + $"or this page is no longer a secret. Remove {disclosing} from the endpoint and reload this page, "
                          + "and treat whatever was in it as disclosed.",
                Endpoint = endpoint
            };
        }

        // Everything from here onwards is the shared mechanism: probe, version- and seccomp-check, compare against the
        // pin. What stays here is the prose, because ADR 0004 makes these messages the entire Development Mode
        // experience for a user with no daemon, and the second consumer of the same daemon owes its own users
        // different words for the same status.
        var outcome = await DockerDaemonProbe.RunAsync(new DockerDaemonProbeRequest
            {
                ConfiguredEndpoint = options.DaemonEndpoint,
                ResolvedEndpoint = endpoint,
                MinimumApiVersion = options.MinimumApiVersion,
                RequireSeccompSupport = true,
                ConfirmingDaemonId = confirmingDaemonId
            },
            _clientFactory.Create,
            _attestationStore,
            _timeProvider,
            _logger,
            cancellationToken);

        // Switched on the reason rather than on the status: two refusals share ProbeFailed and have different prose,
        // so a mapping keyed on status alone would have to guess between them. Every named reason has its own arm and
        // the catch-all only throws — the compiler demands one for an out-of-range cast (CS8524) and would reject the
        // switch outright if a named reason were missing, so nothing here can silently inherit another case's words.
        // `DockerDaemonProbeTests.EveryProbeReason_IsMappedToItsOwnDevelopmentModeMessage` enumerates the enum, so a
        // reason added without an arm is a red test rather than a throw a user meets first.
        return outcome.Reason switch
        {
            DockerDaemonProbeReason.Ready => Ready(outcome.ObservedDaemon!, outcome.Endpoint, outcome.PinnedDaemon!),
            DockerDaemonProbeReason.TransportFailure => DescribeTransportFailure(outcome),
            DockerDaemonProbeReason.ApiVersionTooOld => DescribeApiVersionTooOld(outcome),
            DockerDaemonProbeReason.SeccompUnsupported => DescribeSeccompUnsupported(outcome),
            DockerDaemonProbeReason.ConfirmationRaced => DescribeConfirmationRace(outcome, confirmingDaemonId),
            DockerDaemonProbeReason.IdentityChanged => DescribeIdentityChange(outcome),
            _ => throw new InvalidOperationException($"Unhandled daemon probe reason '{outcome.Reason}'.")
        };
    }

    private DockerDaemonPreflight DescribeTransportFailure(DockerDaemonProbeOutcome outcome)
    {
        var exception = outcome.ProbeFailure!;

        _logger.LogInformation(exception,
            "Development Mode container-runtime preflight failed at {Endpoint} with {Status}.",
            outcome.Endpoint.Display,
            exception.Status);

        return new DockerDaemonPreflight
        {
            Status = exception.Status,
            Message = DescribeProbeFailure(exception, outcome.Endpoint),
            Endpoint = outcome.Endpoint,
            PinnedDaemon = outcome.PinnedDaemon
        };
    }

    private static DockerDaemonPreflight DescribeApiVersionTooOld(DockerDaemonProbeOutcome outcome)
    {
        var identity = outcome.ObservedDaemon!;

        return new DockerDaemonPreflight
        {
            Status = DockerDaemonPreflightStatus.ApiVersionTooOld,
            Message = $"The container runtime at {outcome.Endpoint.Display} reports Docker Engine {Describe(identity.ServerVersion)} "
                      + $"serving API {Describe(identity.ApiVersion)}. Development Mode needs API {outcome.RequiredApiVersion} or newer, "
                      + "because below that it cannot read back every isolation setting it applies — and it refuses to run your "
                      + "code in a container it cannot prove is confined. Upgrade Docker Engine, then reload this page.",
            Endpoint = outcome.Endpoint,
            ObservedDaemon = identity,
            PinnedDaemon = outcome.PinnedDaemon
        };
    }

    private static DockerDaemonPreflight DescribeSeccompUnsupported(DockerDaemonProbeOutcome outcome)
    {
        return new DockerDaemonPreflight
        {
            Status = DockerDaemonPreflightStatus.ProbeFailed,
            Message = $"The container runtime at {outcome.Endpoint.Display} does not report seccomp support, so Development Mode cannot "
                      + "confine the system calls your build and test commands may make. This is checked here rather than at "
                      + "container creation because it cannot be checked there: such a daemon still accepts a seccomp profile and "
                      + "still reports it back on the container, while applying nothing. Either the daemon was started with seccomp "
                      + "disabled (check 'docker info' — a working daemon lists 'seccomp' under Security Options) or this kernel "
                      + "was built without CONFIG_SECCOMP. Development Mode stays unavailable until it is available; it does not "
                      + "fall back to an unconfined container.",
            Endpoint = outcome.Endpoint,
            ObservedDaemon = outcome.ObservedDaemon,
            PinnedDaemon = outcome.PinnedDaemon
        };
    }

    private static DockerDaemonPreflight DescribeConfirmationRace(DockerDaemonProbeOutcome outcome, string? confirmingDaemonId)
    {
        var identity = outcome.ObservedDaemon!;

        return new DockerDaemonPreflight
        {
            Status = DockerDaemonPreflightStatus.DaemonIdentityChanged,
            Message = "That confirmation was not applied. It approved container runtime "
                      + $"{Describe(confirmingDaemonId)}, but the runtime reachable now is {Describe(identity.DaemonId)} — "
                      + "the daemon changed again between the moment you were shown it and the moment you confirmed. "
                      + "Nothing was approved. Review the runtime below and confirm again if it is the one you intend.",
            Endpoint = outcome.Endpoint,
            ObservedDaemon = identity,
            PinnedDaemon = outcome.PinnedDaemon
        };
    }

    private static DockerDaemonPreflight DescribeIdentityChange(DockerDaemonProbeOutcome outcome)
    {
        var identity = outcome.ObservedDaemon!;
        var pinned = outcome.PinnedDaemon!;

        return new DockerDaemonPreflight
        {
            Status = DockerDaemonPreflightStatus.DaemonIdentityChanged,
            Message = "Development Mode is pinned to a different container runtime than the one it can reach now, and it will "
                      + "not use the new one until you say so. "
                      + $"Approved {FormatTimestamp(pinned.ConfirmedAtUtc)}: runtime {Describe(pinned.DaemonId)} "
                      + $"(Docker Engine {Describe(pinned.ServerVersion)}) at {pinned.Endpoint}, found via {Describe(pinned.EndpointSource)}. "
                      + $"Reachable now: runtime {Describe(identity.DaemonId)} (Docker Engine {Describe(identity.ServerVersion)}) "
                      + $"at {outcome.Endpoint.Display}, found via {Describe(outcome.Endpoint.Source)}. "
                      + "DOCKER_HOST is an ordinary environment variable, so a changed runtime can mean a changed machine: your "
                      + "repository would be mounted into, and your build and test commands executed by, something you have not "
                      + "approved. Confirm the runtime below if it is the one you intend, or restore the previous DOCKER_HOST and reload.",
            Endpoint = outcome.Endpoint,
            ObservedDaemon = identity,
            PinnedDaemon = pinned
        };
    }

    private static DockerDaemonPreflight Ready(DockerDaemonIdentity identity, DockerDaemonEndpoint endpoint, DockerDaemonAttestation attestation)
    {
        return new DockerDaemonPreflight
        {
            Status = DockerDaemonPreflightStatus.Ready,
            Message = $"Container runtime ready: Docker Engine {Describe(identity.ServerVersion)} (API {Describe(identity.ApiVersion)}) "
                      + $"at {endpoint.Display}, found via {Describe(endpoint.Source)}. This node approved runtime "
                      + $"{Describe(identity.DaemonId)} {FormatTimestamp(attestation.ConfirmedAtUtc)}"
                      + (attestation.ConfirmedByOperator ? " and you confirmed it." : " on first use."),
            Endpoint = endpoint,
            ObservedDaemon = identity,
            PinnedDaemon = attestation
        };
    }

    private static string DescribeProbeFailure(DockerRuntimeException exception, DockerDaemonEndpoint endpoint)
    {
        var where = $"{endpoint.Display} (found via {Describe(endpoint.Source)})";

        return exception.Status switch
        {
            DockerDaemonPreflightStatus.DaemonUnreachable =>
                $"Development Mode needs a running container runtime and could not reach one at {where}. "
                + "Start the Docker daemon, or set DOCKER_HOST to the socket you want this node to use, then reload this page. "
                + "There is no unisolated fallback by design: rather than quietly running your repository's build and test "
                + "commands directly on this machine, Development Mode stays unavailable until a runtime is reachable.",

            DockerDaemonPreflightStatus.PermissionDenied =>
                $"Development Mode found a container runtime at {where} but this node is not permitted to use it. "
                + "The account running XE-Local-AI-Engine needs read and write access to that socket. Be aware of what you are "
                + "granting: on Linux, access to the Docker socket is equivalent to root on this machine. That is a property of "
                + "the socket, documented here rather than mitigated — grant it only if you accept it.",

            _ =>
                $"Development Mode could not complete its container-runtime preflight against {where}: {exception.Message} "
                + "Development Mode stays unavailable until the preflight succeeds; it does not fall back to running your build "
                + "and test commands outside a container."
        };
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return "on " + value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
    }

    private static string Describe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }

    private static string Describe(DockerDaemonEndpointSource source)
    {
        return source switch
        {
            DockerDaemonEndpointSource.Configuration => "this node's configuration",
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable => "the DOCKER_HOST environment variable",
            DockerDaemonEndpointSource.DefaultUnixSocket => "the default Docker socket",
            DockerDaemonEndpointSource.UserRuntimeUnixSocket => "a per-user Docker socket",
            DockerDaemonEndpointSource.WindowsNamedPipe => "the default Windows Docker pipe",
            _ => "an unrecognised source"
        };
    }
}
