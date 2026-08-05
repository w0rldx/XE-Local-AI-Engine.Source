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
        return await EvaluateAsync(confirmingDaemonId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DockerDaemonPreflight> ConfirmAsync(string expectedDaemonId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDaemonId);
        return await EvaluateAsync(expectedDaemonId, cancellationToken).ConfigureAwait(false);
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

        var endpoint = DockerDaemonEndpointResolver.Resolve(options);
        var pinned = await _attestationStore.ReadAsync(cancellationToken).ConfigureAwait(false);

        DockerDaemonIdentity identity;
        await using (var client = _clientFactory.Create(endpoint))
        {
            try
            {
                identity = await client.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DockerRuntimeException exception)
            {
                _logger.LogInformation(exception,
                    "Development Mode container-runtime preflight failed at {Endpoint} with {Status}.",
                    endpoint.Display,
                    exception.Status);

                return new DockerDaemonPreflight
                {
                    Status = exception.Status,
                    Message = DescribeProbeFailure(exception, endpoint),
                    Endpoint = endpoint,
                    PinnedDaemon = pinned
                };
            }
        }

        if (!MeetsMinimumApiVersion(identity, options, out var minimumApiVersion))
        {
            return new DockerDaemonPreflight
            {
                Status = DockerDaemonPreflightStatus.ApiVersionTooOld,
                Message = $"The container runtime at {endpoint.Display} reports Docker Engine {Describe(identity.ServerVersion)} "
                          + $"serving API {Describe(identity.ApiVersion)}. Development Mode needs API {minimumApiVersion} or newer, "
                          + "because below that it cannot read back every isolation setting it applies — and it refuses to run your "
                          + "code in a container it cannot prove is confined. Upgrade Docker Engine, then reload this page.",
                Endpoint = endpoint,
                ObservedDaemon = identity,
                PinnedDaemon = pinned
            };
        }

        // Trust-on-first-use is the pin, not a check: there is nothing to compare a first daemon
        // against. What it buys is that every subsequent run has something to compare against, which is where the
        // control actually bites.
        if (pinned is null)
        {
            var firstUse = BuildAttestation(identity, endpoint, confirmedByOperator: confirmingDaemonId is not null);
            await _attestationStore.WriteAsync(firstUse, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Pinned Docker daemon {DaemonId} at {Endpoint} on first use.", identity.DaemonId, endpoint.Display);
            return Ready(identity, endpoint, firstUse);
        }

        if (pinned.Matches(identity))
        {
            return Ready(identity, endpoint, pinned);
        }

        if (confirmingDaemonId is not null)
        {
            if (!string.Equals(confirmingDaemonId, identity.DaemonId, StringComparison.Ordinal))
            {
                return new DockerDaemonPreflight
                {
                    Status = DockerDaemonPreflightStatus.DaemonIdentityChanged,
                    Message = "That confirmation was not applied. It approved container runtime "
                              + $"{Describe(confirmingDaemonId)}, but the runtime reachable now is {Describe(identity.DaemonId)} — "
                              + "the daemon changed again between the moment you were shown it and the moment you confirmed. "
                              + "Nothing was approved. Review the runtime below and confirm again if it is the one you intend.",
                    Endpoint = endpoint,
                    ObservedDaemon = identity,
                    PinnedDaemon = pinned
                };
            }

            var confirmed = BuildAttestation(identity, endpoint, confirmedByOperator: true);
            await _attestationStore.WriteAsync(confirmed, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Operator re-confirmed the Docker daemon: {PreviousDaemonId} replaced by {DaemonId} at {Endpoint}.",
                pinned.DaemonId,
                identity.DaemonId,
                endpoint.Display);
            return Ready(identity, endpoint, confirmed);
        }

        return new DockerDaemonPreflight
        {
            Status = DockerDaemonPreflightStatus.DaemonIdentityChanged,
            Message = "Development Mode is pinned to a different container runtime than the one it can reach now, and it will "
                      + "not use the new one until you say so. "
                      + $"Approved {FormatTimestamp(pinned.ConfirmedAtUtc)}: runtime {Describe(pinned.DaemonId)} "
                      + $"(Docker Engine {Describe(pinned.ServerVersion)}) at {pinned.Endpoint}, found via {Describe(pinned.EndpointSource)}. "
                      + $"Reachable now: runtime {Describe(identity.DaemonId)} (Docker Engine {Describe(identity.ServerVersion)}) "
                      + $"at {endpoint.Display}, found via {Describe(endpoint.Source)}. "
                      + "DOCKER_HOST is an ordinary environment variable, so a changed runtime can mean a changed machine: your "
                      + "repository would be mounted into, and your build and test commands executed by, something you have not "
                      + "approved. Confirm the runtime below if it is the one you intend, or restore the previous DOCKER_HOST and reload.",
            Endpoint = endpoint,
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

    private static bool MeetsMinimumApiVersion(DockerDaemonIdentity identity, ContainerSandboxOptions options, out string minimumApiVersion)
    {
        minimumApiVersion = options.MinimumApiVersion;

        if (!ContainerSandboxOptionsValidator.TryParseApiVersion(options.MinimumApiVersion, out var minimum))
        {
            // An unparsable minimum is a configuration fault the validator already rejects at startup. Treating it as
            // "satisfied" here would be the wrong direction for a fail-closed control, so treat it as unsatisfied.
            return false;
        }

        return ContainerSandboxOptionsValidator.TryParseApiVersion(identity.ApiVersion, out var observed)
               && ContainerSandboxOptionsValidator.IsApiVersionAtLeast(observed, minimum);
    }

    private DockerDaemonAttestation BuildAttestation(DockerDaemonIdentity identity, DockerDaemonEndpoint endpoint, bool confirmedByOperator)
    {
        return new DockerDaemonAttestation
        {
            DaemonId = identity.DaemonId,
            Endpoint = endpoint.Display,
            EndpointSource = endpoint.Source,
            ServerVersion = identity.ServerVersion,
            ConfirmedAtUtc = _timeProvider.GetUtcNow(),
            ConfirmedByOperator = confirmedByOperator
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
