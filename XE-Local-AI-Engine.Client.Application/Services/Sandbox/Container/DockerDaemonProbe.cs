namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>Why a <see cref="DockerDaemonProbeOutcome" /> carries the status it carries.</summary>
/// <remarks>
///     Two different refusals share one status: a classified transport failure and a daemon that does not report seccomp are both
///     <see cref="DockerDaemonPreflightStatus.ProbeFailed" />, each with its own operator prose. Without this enum they would be told
///     apart only by whether <see cref="DockerDaemonProbeOutcome.ProbeFailure" /> happened to be null, resting a verbatim-message
///     guarantee on an implicit signal. A consumer switches on this, so a new refusal has to be given prose rather than inheriting
///     someone else's.
/// </remarks>
internal enum DockerDaemonProbeReason
{
    /// <summary>A daemon answered, is new enough, reports what was required, and matches this node's pin.</summary>
    Ready = 0,

    /// <summary>The probe call itself failed and was classified; <see cref="DockerDaemonProbeOutcome.ProbeFailure" /> carries it.</summary>
    TransportFailure = 1,

    /// <summary>A daemon answered but does not report seccomp support, and the caller required it.</summary>
    SeccompUnsupported = 2,

    /// <summary>A daemon answered but serves an API older than the caller's minimum.</summary>
    ApiVersionTooOld = 3,

    /// <summary>A daemon answered and it is not the one this node pinned, and no confirmation was offered.</summary>
    IdentityChanged = 4,

    /// <summary>
    ///     A confirmation named a daemon other than the one reachable now, so nothing was approved — whether or not a pin exists and
    ///     whether or not the pin already matches the reachable daemon.
    /// </summary>
    ConfirmationRaced = 5
}

/// <summary>What one consumer asks the shared daemon probe to check.</summary>
/// <remarks>
///     Deliberately a plain record rather than any consumer's options type: the probe is shared by Development Mode and the
///     application-container runtime, and a parameter named for one would make the other's timeout or minimum version dead configuration.
/// </remarks>
internal sealed record DockerDaemonProbeRequest
{
    /// <summary>The consumer's explicit endpoint setting, or null to let discovery name one.</summary>
    public required string? ConfiguredEndpoint { get; init; }

    /// <summary>
    ///     The endpoint the consumer already resolved, when it resolved one itself; null runs discovery here, as Development Mode does.
    /// </summary>
    /// <remarks>
    ///     A consumer that resolves first and re-states its result as a string loses which <see cref="DockerDaemonEndpointSource" /> named
    ///     it: every value round-trips through the resolver as an explicit setting and comes back
    ///     <see cref="DockerDaemonEndpointSource.Configuration" />. That source is written into the shared trust-on-first-use pin an
    ///     operator is shown when a daemon is substituted, so a wrong source is a wrong sentence about which socket this node approved.
    /// </remarks>
    public DockerDaemonEndpoint? ResolvedEndpoint { get; init; }

    /// <summary>The oldest Docker Engine API version this consumer accepts, as <c>major.minor</c>.</summary>
    public required string MinimumApiVersion { get; init; }

    /// <summary>
    ///     Whether a daemon that does not report seccomp support is refused. Development Mode requires it because it
    ///     creates containers under an engine seccomp profile that such a daemon would accept and silently not apply.
    /// </summary>
    public required bool RequireSeccompSupport { get; init; }

    /// <summary>
    ///     The daemon id the operator was shown, when this probe is an explicit confirmation rather than a read. Null
    ///     for a read. A confirmation naming a daemon other than the reachable one approves nothing.
    /// </summary>
    public string? ConfirmingDaemonId { get; init; }
}

/// <summary>The evidence one daemon probe produced, and nothing else.</summary>
/// <remarks>
///     There is deliberately no message: the prose an operator reads is written by the consumer, because the same status means different
///     things to a user who has lost Development Mode and to one whose installed application will not start.
/// </remarks>
internal sealed record DockerDaemonProbeOutcome
{
    /// <summary>The classified outcome, in the vocabulary both consumers already speak.</summary>
    public required DockerDaemonPreflightStatus Status { get; init; }

    /// <summary>Which refusal produced <see cref="Status" />, so a consumer's mapping is exhaustive rather than implicit.</summary>
    public required DockerDaemonProbeReason Reason { get; init; }

    /// <summary>The endpoint the probe used. Always present: resolution names an endpoint even when nothing answers there.</summary>
    public required DockerDaemonEndpoint Endpoint { get; init; }

    /// <summary>The daemon actually reached, when one answered.</summary>
    public DockerDaemonIdentity? ObservedDaemon { get; init; }

    /// <summary>This node's pinned daemon, when it has one — including the pin this probe just wrote.</summary>
    public DockerDaemonAttestation? PinnedDaemon { get; init; }

    /// <summary>The classified transport failure, set only for <see cref="DockerDaemonProbeReason.TransportFailure" />.</summary>
    public DockerRuntimeException? ProbeFailure { get; init; }

    /// <summary>The minimum API version that was not met, set only for <see cref="DockerDaemonProbeReason.ApiVersionTooOld" />.</summary>
    public string? RequiredApiVersion { get; init; }
}

/// <summary>
///     The mechanism behind every Docker daemon preflight: resolve an endpoint, probe the daemon, check the API version and seccomp
///     support, and compare what answered against this node's trust-on-first-use pin.
/// </summary>
/// <remarks>
///     A static helper, not an injected service: Development Mode's preflight holds the operator prose ADR 0004 treats as the feature for
///     a user with no daemon, and its constructor, interface and messages must not move because a second consumer appeared. The client
///     comes from a caller-supplied delegate for the same reason — an injected factory would be whichever the container registered,
///     making a second consumer's timeout dead configuration.
/// </remarks>
internal static class DockerDaemonProbe
{
    /// <summary>Serialises the compare-and-write half of the transaction, process-wide, and nothing else.</summary>
    /// <remarks>
    ///     The attestation store locks one operation at a time, which is not the same thing: two first-use probes on independent schedules
    ///     against one shared pin can both read it absent, each approve whichever daemon answered, and each write over the other, so
    ///     trust-on-first-use approves two daemons and remembers one by a race. Static because the pin is a per-node singleton. It does
    ///     NOT span the daemon call, which is no read-compare-write and whose inclusion made every probe wait out another's timeout; every
    ///     approving path still enters the gate, comparing a pin read AFTER the daemon answered.
    /// </remarks>
    private static readonly SemaphoreSlim Transaction = new(initialCount: 1, maxCount: 1);

    /// <summary>Resolve, probe, and compare against the pin, writing the pin on first use or on a matching confirmation.</summary>
    public static async Task<DockerDaemonProbeOutcome> RunAsync(DockerDaemonProbeRequest request,
        Func<DockerDaemonEndpoint, IDockerRuntimeClient> clientFactory,
        IDockerDaemonAttestationStore attestationStore,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(attestationStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        var endpoint = request.ResolvedEndpoint ?? DockerDaemonEndpointResolver.Resolve(request.ConfiguredEndpoint);

        // Read outside the gate, used only by the three refusals that return before it, where it is reported and never acted on; every
        // approving path re-reads the pin inside CommitAsync.
        var pinned = await attestationStore.ReadAsync(cancellationToken);

        DockerDaemonIdentity identity;
        await using (var client = clientFactory(endpoint))
        {
            try
            {
                identity = await client.ProbeAsync(cancellationToken);
            }
            catch (DockerRuntimeException exception)
            {
                return new DockerDaemonProbeOutcome
                {
                    Status = exception.Status,
                    Reason = DockerDaemonProbeReason.TransportFailure,
                    Endpoint = endpoint,
                    PinnedDaemon = pinned,
                    ProbeFailure = exception
                };
            }
        }

        if (!MeetsMinimumApiVersion(identity, request.MinimumApiVersion))
        {
            return new DockerDaemonProbeOutcome
            {
                Status = DockerDaemonPreflightStatus.ApiVersionTooOld,
                Reason = DockerDaemonProbeReason.ApiVersionTooOld,
                Endpoint = endpoint,
                ObservedDaemon = identity,
                PinnedDaemon = pinned,
                RequiredApiVersion = request.MinimumApiVersion
            };
        }

        if (request.RequireSeccompSupport && !identity.SupportsSeccomp)
        {
            return new DockerDaemonProbeOutcome
            {
                Status = DockerDaemonPreflightStatus.ProbeFailed,
                Reason = DockerDaemonProbeReason.SeccompUnsupported,
                Endpoint = endpoint,
                ObservedDaemon = identity,
                PinnedDaemon = pinned
            };
        }

        // Every outcome approving a daemon is decided inside the gate against a pin read AFTER the daemon answered: deciding out here on
        // the pre-call read would approve a daemon this node no longer trusts, a concurrent confirmation being able to move the pin.
        await Transaction.WaitAsync(cancellationToken);
        try
        {
            return await CommitAsync(request, endpoint, identity, attestationStore, timeProvider, logger, cancellationToken);
        }
        finally
        {
            Transaction.Release();
        }
    }

    /// <summary>
    ///     The read-compare-write half, and the only place a daemon is compared against the pin, entered under <see cref="Transaction" />.
    /// </summary>
    /// <remarks>
    ///     The pin is read here rather than reused from the caller's earlier read, which happened before the daemon call, so a concurrent
    ///     probe may have pinned or re-confirmed since: the loser of a first-use race must observe the winner's pin instead of writing
    ///     over it, and a probe whose pin matched before the call must not report Ready for a daemon it has since moved away from.
    /// </remarks>
    private static async Task<DockerDaemonProbeOutcome> CommitAsync(DockerDaemonProbeRequest request,
        DockerDaemonEndpoint endpoint,
        DockerDaemonIdentity identity,
        IDockerDaemonAttestationStore attestationStore,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var pinned = await attestationStore.ReadAsync(cancellationToken);

        // A confirmation binds to the daemon the operator was shown, before any other branch: checked only on the substitution path, a
        // bogus id answered Ready whenever the pin already matched, and on first use any non-blank id pinned whatever answered.
        if (request.ConfirmingDaemonId is not null
            && !string.Equals(request.ConfirmingDaemonId, identity.DaemonId, StringComparison.Ordinal))
        {
            return new DockerDaemonProbeOutcome
            {
                Status = DockerDaemonPreflightStatus.DaemonIdentityChanged,
                Reason = DockerDaemonProbeReason.ConfirmationRaced,
                Endpoint = endpoint,
                ObservedDaemon = identity,
                PinnedDaemon = pinned
            };
        }

        // Trust-on-first-use is the pin, not a check: there is nothing to compare a first daemon against. What it buys is that every
        // subsequent run has something to compare against, which is where the control actually bites.
        if (pinned is null)
        {
            var firstUse = BuildAttestation(identity, endpoint, timeProvider, confirmedByOperator: request.ConfirmingDaemonId is not null);
            await attestationStore.WriteAsync(firstUse, cancellationToken);
            logger.LogInformation("Pinned Docker daemon {DaemonId} at {Endpoint} on first use.", identity.DaemonId, endpoint.Display);
            return Ready(identity, endpoint, firstUse);
        }

        if (pinned.Matches(identity))
        {
            return Ready(identity, endpoint, pinned);
        }

        if (request.ConfirmingDaemonId is not null)
        {
            var confirmed = BuildAttestation(identity, endpoint, timeProvider, confirmedByOperator: true);
            await attestationStore.WriteAsync(confirmed, cancellationToken);
            logger.LogWarning("Operator re-confirmed the Docker daemon: {PreviousDaemonId} replaced by {DaemonId} at {Endpoint}.",
                pinned.DaemonId,
                identity.DaemonId,
                endpoint.Display);
            return Ready(identity, endpoint, confirmed);
        }

        return new DockerDaemonProbeOutcome
        {
            Status = DockerDaemonPreflightStatus.DaemonIdentityChanged,
            Reason = DockerDaemonProbeReason.IdentityChanged,
            Endpoint = endpoint,
            ObservedDaemon = identity,
            PinnedDaemon = pinned
        };
    }

    private static DockerDaemonProbeOutcome Ready(DockerDaemonIdentity identity,
        DockerDaemonEndpoint endpoint,
        DockerDaemonAttestation attestation)
    {
        return new DockerDaemonProbeOutcome
        {
            Status = DockerDaemonPreflightStatus.Ready,
            Reason = DockerDaemonProbeReason.Ready,
            Endpoint = endpoint,
            ObservedDaemon = identity,
            PinnedDaemon = attestation
        };
    }

    private static bool MeetsMinimumApiVersion(DockerDaemonIdentity identity, string minimumApiVersion)
    {
        if (!ContainerSandboxOptionsValidator.TryParseApiVersion(minimumApiVersion, out var minimum))
        {
            // An unparsable minimum is a configuration fault the validator already rejects at startup. Treating it as
            // "satisfied" here would be the wrong direction for a fail-closed control, so treat it as unsatisfied.
            return false;
        }

        return ContainerSandboxOptionsValidator.TryParseApiVersion(identity.ApiVersion, out var observed)
               && ContainerSandboxOptionsValidator.IsApiVersionAtLeast(observed, minimum);
    }

    private static DockerDaemonAttestation BuildAttestation(DockerDaemonIdentity identity,
        DockerDaemonEndpoint endpoint,
        TimeProvider timeProvider,
        bool confirmedByOperator)
    {
        return new DockerDaemonAttestation
        {
            DaemonId = identity.DaemonId,
            Endpoint = endpoint.Display,
            EndpointSource = endpoint.Source,
            ServerVersion = identity.ServerVersion,
            ConfirmedAtUtc = timeProvider.GetUtcNow(),
            ConfirmedByOperator = confirmedByOperator
        };
    }
}
