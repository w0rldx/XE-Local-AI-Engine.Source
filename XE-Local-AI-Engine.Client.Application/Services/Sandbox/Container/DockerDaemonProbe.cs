namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

/// <summary>
///     Why a <see cref="DockerDaemonProbeOutcome" /> carries the status it carries.
///     <para>
///         It exists because two different refusals share one status. A classified transport failure and a daemon that
///         does not report seccomp are both <see cref="DockerDaemonPreflightStatus.ProbeFailed" />, and each has its
///         own operator prose in <c>DockerDaemonPreflightService</c>. Without this enum the two would be told apart
///         only by whether <see cref="DockerDaemonProbeOutcome.ProbeFailure" /> happened to be null — resting a
///         verbatim-message guarantee on an implicit signal, which is how a message quietly changes later. A consumer
///         switches on this, so a new refusal has to be given prose rather than inheriting someone else's.
///     </para>
/// </summary>
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

    /// <summary>A confirmation was offered for a daemon other than the one reachable now, so nothing was approved.</summary>
    ConfirmationRaced = 5
}

/// <summary>
///     What one consumer asks the shared daemon probe to check. Deliberately a plain record rather than any consumer's
///     options type: the probe is shared by Development Mode and the application-container runtime, and a parameter
///     named for one of them would make the other's timeout or minimum version dead configuration.
/// </summary>
internal sealed record DockerDaemonProbeRequest
{
    /// <summary>The consumer's explicit endpoint setting, or null to let discovery name one.</summary>
    public required string? ConfiguredEndpoint { get; init; }

    /// <summary>
    ///     The endpoint the consumer has already resolved, when it resolved one itself. Null means "run discovery
    ///     here", which is what Development Mode does.
    ///     <para>
    ///         A consumer that resolves first and then re-states its result as a string loses which of
    ///         <see cref="DockerDaemonEndpointSource" /> named it: every value round-trips through
    ///         <see cref="DockerDaemonEndpointResolver" /> as an explicit setting and comes back
    ///         <see cref="DockerDaemonEndpointSource.Configuration" />. That source is written into the shared
    ///         trust-on-first-use pin, which an operator is later shown when a daemon is substituted, so the wrong
    ///         source is a wrong sentence about which socket this node approved. Passing the endpoint itself keeps the
    ///         source the consumer actually observed.
    ///     </para>
    /// </summary>
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

/// <summary>
///     The evidence one daemon probe produced, and nothing else. There is deliberately no message here: the prose an
///     operator reads is written by the consumer, because the same status means different things to a user who has
///     lost Development Mode and to one whose installed application will not start.
/// </summary>
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
///     The mechanism behind every Docker daemon preflight in this engine: resolve an endpoint, probe the daemon, check
///     the API version and seccomp support, and compare what answered against this node's trust-on-first-use pin.
///     <para>
///         A static helper rather than an injected service on purpose. Development Mode's preflight is the home of
///         operator prose that ADR 0004 treats as the feature for a user with no daemon, and its constructor, its
///         interface and its message strings must not move because a second consumer appeared. So the mechanism is
///         extracted and the prose is not: this class produces status and evidence, each consumer writes its own
///         words.
///     </para>
///     <para>
///         The client is created through a caller-supplied delegate rather than an <see cref="IDockerRuntimeClientFactory" />
///         for the same reason: an injected factory would be whichever one the container is registered with, so a
///         second consumer's probe timeout would be configuration nothing reads. The delegate makes each consumer
///         supply the client it means.
///     </para>
/// </summary>
internal static class DockerDaemonProbe
{
    /// <summary>
    ///     Serialises the compare-and-write half of the transaction, process-wide, and nothing else.
    ///     <para>
    ///         The attestation store locks one operation at a time, which is not the same thing. Two first-use probes
    ///         — Development Mode's preflight and the application-container resolver, which run on independent
    ///         schedules against the same shared pin — can both read an absent pin, each approve whichever daemon
    ///         answered it, and each write over the other. Trust-on-first-use then approved two daemons and remembers
    ///         one, chosen by a race. Static because the pin is a per-node singleton: an injected gate would be one
    ///         instance per consumer, which is exactly the isolation that lets the race happen.
    ///     </para>
    ///     <para>
    ///         It deliberately does NOT span the daemon probe. That hazard is a read-compare-write on the pin; the
    ///         daemon call is neither, and holding the gate across it made every probe wait out every other probe's
    ///         transport timeout. Two features probe the same daemon on independent schedules, so a Development Mode
    ///         page load landing beside the application-container resolver queued for one <c>DaemonProbeTimeoutSeconds</c>
    ///         before spending its own. Every probe that reaches a daemon does enter the gate, because the compare
    ///         that decides whether this node approves it has to happen on a pin read after the daemon answered —
    ///         but it holds only store operations, never the call.
    ///     </para>
    /// </summary>
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

        // Read outside the gate, and used by nothing but the three refusals that return before it — transport
        // failure, API too old, seccomp unsupported — where it is reported and never acted on. Those may carry a pin
        // that has since moved; re-reading it would mean taking the gate on paths that approve nothing, which is the
        // contention the gate is kept short to avoid. Every path that does approve re-reads inside CommitAsync.
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

        // Every outcome that approves a daemon — a match, a first-use pin, a confirmation, or the refusal when none
        // of those hold — is decided inside the gate, against a pin read AFTER the daemon answered. Deciding a match
        // out here against the pre-call read would approve a daemon this node no longer trusts: a concurrent
        // confirmation can move the pin while this probe's transport call is in flight, and the resolver would then
        // cache Ready for the superseded daemon instead of reporting DaemonIdentityChanged. The gate still holds no
        // daemon I/O, so a matching probe waits only out the other probe's store operations, never its timeout.
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
    ///     The read-compare-write half, and the only place a daemon is compared against the pin, entered under
    ///     <see cref="Transaction" />. The pin is read here rather than reused from the caller's earlier read: that
    ///     read happened before the daemon call, so a concurrent probe may have pinned or re-confirmed a daemon
    ///     since. The loser of a first-use race must observe the winner's pin instead of writing over it, and a probe
    ///     whose pin matched before the call must not report Ready for a daemon the pin has since moved away from.
    /// </summary>
    private static async Task<DockerDaemonProbeOutcome> CommitAsync(DockerDaemonProbeRequest request,
        DockerDaemonEndpoint endpoint,
        DockerDaemonIdentity identity,
        IDockerDaemonAttestationStore attestationStore,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var pinned = await attestationStore.ReadAsync(cancellationToken);

        // Trust-on-first-use is the pin, not a check: there is nothing to compare a first daemon
        // against. What it buys is that every subsequent run has something to compare against, which is where the
        // control actually bites.
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
            if (!string.Equals(request.ConfirmingDaemonId, identity.DaemonId, StringComparison.Ordinal))
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
