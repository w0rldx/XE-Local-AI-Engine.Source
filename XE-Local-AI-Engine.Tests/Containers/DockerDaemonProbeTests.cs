namespace XE_Local_AI_Engine.Tests.Containers;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.ContainerSandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared daemon probe, tested as what it is: a producer of status and evidence with no operator prose in it.
///     <para>
///         Development Mode's own behaviour is asserted, unchanged, by <c>DockerDaemonPreflightServiceTests</c>, which
///         this refactor must leave untouched. What is asserted here is the half that is now shared — resolution,
///         probe classification, the API and seccomp gates, and the trust-on-first-use pin — plus the two properties
///         the extraction exists to buy: each consumer supplies its own client, and each refusal is distinguishable
///         without reading an exception out of the outcome.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DockerDaemonProbeTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 9, day: 5, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    private const string ConfiguredEndpoint = "unix:///fake-probe.sock";

    [Test]
    public async Task FirstUse_PinsTheDaemonAndReportsReady()
    {
        var (client, store) = Doubles();

        var outcome = await RunAsync(client, store);

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.Ready, outcome.Reason);
        var pinned = AssertEx.NotNull(await store.ReadAsync());
        AssertEx.Equal("daemon-alpha", pinned.DaemonId);
        AssertEx.Equal(FixedNow, pinned.ConfirmedAtUtc);
        // Recorded honestly as a first-use pin rather than as an operator decision.
        AssertEx.False(pinned.ConfirmedByOperator);
        AssertEx.Equal(pinned, outcome.PinnedDaemon);
    }

    [Test]
    public async Task AResolvedEndpoint_IsUsedAsGivenAndCarriesItsOwnSourceIntoThePin()
    {
        // A consumer that resolves the endpoint itself hands the endpoint over rather than restating it as a string.
        // Restating it would send the value back through discovery as an explicit setting, so the pin — which is
        // shared, and which an operator is shown when a daemon is later substituted — would claim engine
        // configuration named a socket that DOCKER_HOST or the platform default actually named.
        var (client, store) = Doubles();
        var resolved = new DockerDaemonEndpoint(new Uri("unix:///fake-probe-from-docker-host.sock"),
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable);

        var outcome = await RunAsync(client, store, resolvedEndpoint: resolved);

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, outcome.Status);
        AssertEx.Equal(resolved, outcome.Endpoint);
        var pinned = AssertEx.NotNull(await store.ReadAsync());
        AssertEx.Equal(DockerDaemonEndpointSource.DockerHostEnvironmentVariable, pinned.EndpointSource);
        AssertEx.Equal(resolved.Display, pinned.Endpoint);
    }

    [Test]
    public async Task NoResolvedEndpoint_StillDiscoversFromTheConfiguredString()
    {
        // Development Mode's path, which leaves the new member null: the probe must keep resolving for itself, or the
        // extraction would have broken the consumer it was extracted from.
        var (client, store) = Doubles();

        var outcome = await RunAsync(client, store);

        AssertEx.Equal(ConfiguredEndpoint, outcome.Endpoint.Display);
        AssertEx.Equal(DockerDaemonEndpointSource.Configuration, outcome.Endpoint.Source);
        AssertEx.Equal(DockerDaemonEndpointSource.Configuration, AssertEx.NotNull(await store.ReadAsync()).EndpointSource);
    }

    [Test]
    [Arguments(DockerDaemonPreflightStatus.DaemonUnreachable)]
    [Arguments(DockerDaemonPreflightStatus.PermissionDenied)]
    [Arguments(DockerDaemonPreflightStatus.ProbeFailed)]
    public async Task AProbeFailure_IsReportedWithItsClassifiedStatus(DockerDaemonPreflightStatus status)
    {
        var (client, store) = Doubles();
        client.ProbeFailure = new DockerRuntimeException(status, "the socket said no");

        var outcome = await RunAsync(client, store);

        AssertEx.Equal(status, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.TransportFailure, outcome.Reason);
        AssertEx.Equal("the socket said no", AssertEx.NotNull(outcome.ProbeFailure).Message);
        // A daemon that never answered must not become the daemon this node approved.
        AssertEx.Null(await store.ReadAsync());
        AssertEx.Equal(expected: 0, store.WriteCount);
    }

    [Test]
    [Arguments("1.40")]
    [Arguments("1.9")]
    public async Task AnApiVersionBelowTheMinimum_IsApiVersionTooOld(string apiVersion)
    {
        // 1.9 precedes 1.41. Read as decimals, 1.9 > 1.41 and an ancient daemon would pass the gate.
        var (client, store) = Doubles();
        client.Identity = client.Identity with
        {
            ApiVersion = apiVersion
        };

        var outcome = await RunAsync(client, store);

        AssertEx.Equal(DockerDaemonPreflightStatus.ApiVersionTooOld, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.ApiVersionTooOld, outcome.Reason);
        // Carried as evidence so the consumer writes "needs API 1.41" without re-reading its own configuration.
        AssertEx.Equal("1.41", outcome.RequiredApiVersion);
        AssertEx.Null(await store.ReadAsync());
    }

    [Test]
    public async Task ADaemonThatDoesNotReportSeccomp_IsRefusedWhenSeccompIsRequired()
    {
        var (client, store) = Doubles();
        client.Identity = client.Identity with
        {
            SupportsSeccomp = false
        };

        var outcome = await RunAsync(client, store, requireSeccompSupport: true);

        AssertEx.Equal(DockerDaemonPreflightStatus.ProbeFailed, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.SeccompUnsupported, outcome.Reason);
        // Refused rather than pinned: a daemon this consumer will not use must not become the daemon it approved.
        AssertEx.Null(await store.ReadAsync());
    }

    [Test]
    public async Task ADaemonThatDoesNotReportSeccomp_IsAcceptedWhenItIsNotRequired()
    {
        // The reason the flag is on the request rather than a constant in the probe: Development Mode creates
        // containers under an engine seccomp profile such a daemon would silently not apply, and refuses. A consumer
        // that makes no such claim has nothing to refuse it for.
        var (client, store) = Doubles();
        client.Identity = client.Identity with
        {
            SupportsSeccomp = false
        };

        var outcome = await RunAsync(client, store, requireSeccompSupport: false);

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.Ready, outcome.Reason);
    }

    [Test]
    public async Task ADifferentDaemonId_IsDaemonIdentityChanged()
    {
        var (client, store) = Doubles();
        await RunAsync(client, store);
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-beta"
        };

        var outcome = await RunAsync(client, store);

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.IdentityChanged, outcome.Reason);
        // The whole control: a read must never approve. Trust-on-first-use pins once; after that only an operator
        // moves the pin.
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(await store.ReadAsync()).DaemonId);
        AssertEx.Equal(expected: 1, store.WriteCount);
    }

    [Test]
    public async Task ASameDaemonAtANewEndpoint_StaysReadyWithoutRewritingThePin()
    {
        // The false-positive direction. Identity is what is pinned; the endpoint is context. A daemon that moved is
        // the same daemon, and prompting for it would train the operator to approve prompts without reading them.
        var (client, store) = Doubles();
        await RunAsync(client, store);
        client.Identity = client.Identity with
        {
            Endpoint = new DockerDaemonEndpoint(new Uri("unix:///run/user/1000/docker.sock"),
                DockerDaemonEndpointSource.UserRuntimeUnixSocket)
        };

        var outcome = await RunAsync(client, store);

        AssertEx.Equal(DockerDaemonProbeReason.Ready, outcome.Reason);
        AssertEx.Equal(expected: 1, store.WriteCount);
    }

    [Test]
    public async Task AConfirmationForADaemonThatMovedAgain_IsNotApplied()
    {
        // Without this check a confirmation issued against one daemon would land on whichever answered next, and the
        // control would approve something nobody looked at — worse than having no confirmation step, because it looks
        // like one.
        var (client, store) = Doubles();
        await RunAsync(client, store);
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-gamma"
        };

        var outcome = await RunAsync(client, store, confirmingDaemonId: "daemon-beta");

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.ConfirmationRaced, outcome.Reason);
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(await store.ReadAsync()).DaemonId);
    }

    [Test]
    public async Task AConfirmationForTheReachableDaemon_ReplacesThePin()
    {
        var (client, store) = Doubles();
        await RunAsync(client, store);
        client.Identity = client.Identity with
        {
            DaemonId = "daemon-beta"
        };

        var outcome = await RunAsync(client, store, confirmingDaemonId: "daemon-beta");

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.Ready, outcome.Reason);
        var pinned = AssertEx.NotNull(await store.ReadAsync());
        AssertEx.Equal("daemon-beta", pinned.DaemonId);
        AssertEx.True(pinned.ConfirmedByOperator);
    }

    [Test]
    public async Task AFirstUseThatIsAlsoAConfirmation_IsRecordedAsAnOperatorDecision()
    {
        var (client, store) = Doubles();

        var outcome = await RunAsync(client, store, confirmingDaemonId: "daemon-alpha");

        AssertEx.Equal(DockerDaemonProbeReason.Ready, outcome.Reason);
        AssertEx.True(AssertEx.NotNull(await store.ReadAsync()).ConfirmedByOperator);
    }

    [Test]
    public void TheProbe_NeverWritesOperatorProse()
    {
        // The extraction's contract, asserted structurally rather than by reading messages. ADR 0004 makes Development
        // Mode's preflight messages the entire experience of a missing daemon, and a second consumer of the same
        // daemon owes its users different words for the same status. A message field here is how one consumer's prose
        // silently becomes the other's.
        var proseCarrying = typeof(DockerDaemonProbeOutcome)
                            .GetProperties()
                            .Where(static property => property.PropertyType == typeof(string))
                            .Select(static property => property.Name)
                            .Where(static name => !string.Equals(name, nameof(DockerDaemonProbeOutcome.RequiredApiVersion), StringComparison.Ordinal))
                            .ToArray();

        AssertEx.Empty(proseCarrying,
            "DockerDaemonProbeOutcome carries evidence, not words. Found string members beyond the version datum: "
            + string.Join(", ", proseCarrying));
    }

    [Test]
    public async Task EveryOutcome_CarriesTheReasonThatExplainsItsStatus()
    {
        // Why DockerDaemonProbeReason exists. Both of these are ProbeFailed and both have their own operator prose, so
        // a consumer mapping on status alone would have to tell them apart by whether ProbeFailure happened to be
        // null — resting a verbatim-message guarantee on an implicit signal.
        var (transportClient, transportStore) = Doubles();
        transportClient.ProbeFailure = new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed, "classified");
        var transport = await RunAsync(transportClient, transportStore);

        var (seccompClient, seccompStore) = Doubles();
        seccompClient.Identity = seccompClient.Identity with
        {
            SupportsSeccomp = false
        };
        var seccomp = await RunAsync(seccompClient, seccompStore, requireSeccompSupport: true);

        AssertEx.Equal(transport.Status, seccomp.Status);
        AssertEx.NotEqual(transport.Reason, seccomp.Reason);
        AssertEx.Equal(DockerDaemonProbeReason.TransportFailure, transport.Reason);
        AssertEx.Equal(DockerDaemonProbeReason.SeccompUnsupported, seccomp.Reason);
    }

    [Test]
    public async Task TheProbe_UsesTheClientCreatorItWasGiven()
    {
        // The reason RunAsync takes a delegate rather than an IDockerRuntimeClientFactory. An injected factory would be
        // whichever one the container registered, so a second consumer's own probe timeout would be configuration
        // nothing reads.
        var (client, store) = Doubles();
        var seen = new List<DockerDaemonEndpoint>();

        var outcome = await DockerDaemonProbe.RunAsync(Request(),
            endpoint =>
            {
                seen.Add(endpoint);
                return client;
            },
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger.Instance);

        AssertEx.Equal(expected: 1, seen.Count);
        AssertEx.Equal(outcome.Endpoint, seen[0]);
        AssertEx.Equal("/fake-probe.sock", seen[0].UnixSocketPath);
    }

    [Test]
    public async Task TwoFirstUseProbesAtOnce_AreSerialisedAndTheSecondSeesTheFirstsPin()
    {
        // Development Mode's preflight and the application-container resolver share one pin and run on independent
        // schedules. The attestation store locks one operation at a time, which leaves the read-compare-write
        // sequence open: both probes read an absent pin, each approves whichever daemon answered it, and the second
        // write silently replaces the first. Trust-on-first-use would then have approved two daemons and remembered
        // whichever won a race.
        var store = new GatedAttestationStore();
        var alpha = ClientFor("daemon-alpha");
        var beta = ClientFor("daemon-beta");

        // The first probe parks inside its own write, so the second is launched while the first transaction is
        // provably still open — which is the window the gate has to close.
        var first = Task.Run(() => RunAsync(alpha, store));
        await store.WriteEntered;
        var second = Task.Run(() => RunAsync(beta, store));

        // The negative half, without a wall-clock guess: with the scheduler drained, the second probe has not got
        // past the gate. Without the gate it would be finished — it would have read the absent pin and written one.
        await AssertEx.StaysIncompleteAsync(second,
            "A second probe ran to completion while the first was still inside its compare-and-write, "
            + "so two first-use pins can be written over each other.");

        store.ReleaseWrite();

        var firstOutcome = await first;
        var secondOutcome = await second;

        AssertEx.Equal(DockerDaemonProbeReason.Ready, firstOutcome.Reason);

        // The ordering, not merely the end state. Each probe reads twice: once before the daemon call, which feeds
        // only the refusals that return before the gate, and once inside the gate, which is the read every approval
        // rests on. The second probe's gated read lands AFTER the first probe's write, so it compares against a pin
        // that exists rather than writing over one it never saw.
        AssertEx.Equal("read, read, write, read, read", string.Join(", ", store.Events));

        // The second probe did not pin a daemon of its own. It compared against the first probe's pin and refused.
        AssertEx.Equal(expected: 1, store.WriteCount);
        AssertEx.Equal(DockerDaemonProbeReason.IdentityChanged, secondOutcome.Reason);
        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, secondOutcome.Status);
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(secondOutcome.PinnedDaemon).DaemonId);
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(await store.ReadAsync()).DaemonId);
    }

    [Test]
    public async Task TwoFirstUseProbesOfTheSameDaemon_PinItOnceAndBothSeeThatPin()
    {
        // The race the gate exists for, in its likeliest shape: one daemon, two features reaching it for the first
        // time at once. Both must end up on the same pin, written once. The first probe's read now happens outside
        // the gate, so the compare that decides whether to write has to be the one inside it.
        var store = new GatedAttestationStore();

        var first = Task.Run(() => RunAsync(ClientFor("daemon-alpha"), store));
        await store.WriteEntered;
        var second = Task.Run(() => RunAsync(ClientFor("daemon-alpha"), store));

        await AssertEx.StaysIncompleteAsync(second,
            "A second first-use probe ran to completion while the first was still inside its compare-and-write.");

        store.ReleaseWrite();

        var firstOutcome = await first;
        var secondOutcome = await second;

        // Trust-on-first-use approved one daemon and remembers it, rather than approving it twice and remembering
        // whichever write landed last.
        AssertEx.Equal(expected: 1, store.WriteCount);
        AssertEx.Equal(DockerDaemonProbeReason.Ready, firstOutcome.Reason);
        AssertEx.Equal(DockerDaemonProbeReason.Ready, secondOutcome.Reason);

        var pinned = AssertEx.NotNull(await store.ReadAsync());
        AssertEx.Equal(pinned, AssertEx.NotNull(firstOutcome.PinnedDaemon));
        AssertEx.Equal(pinned, AssertEx.NotNull(secondOutcome.PinnedDaemon));
    }

    [Test]
    public async Task AProbeWhosePinAlreadyMatches_DoesNotWaitForAnInFlightProbe()
    {
        // Why the gate stops short of the daemon call. Development Mode's page-load preflight and the
        // application-container resolver probe the same daemon on independent schedules; a gate held across the call
        // made each of them wait out the other's transport timeout — two of them, with the daemon unreachable, is a
        // page load stalled for twice DaemonProbeTimeoutSeconds before it learns anything. The matching probe does
        // enter the gate, because its compare has to run on a pin read after the daemon answered, but the gate holds
        // store operations only, so what it can wait on is bounded by those rather than by anyone's timeout.
        var store = new InMemoryDaemonAttestationStore();
        store.Seed(Pin("daemon-alpha"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkedClient = Substitute.For<IDockerRuntimeClient>();
        parkedClient.ProbeAsync(Arg.Any<CancellationToken>()).Returns(_ => ParkAsync());

        var parked = Task.Run(() => DockerDaemonProbe.RunAsync(Request(),
            _ => parkedClient,
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger.Instance));

        await entered.Task;

        var matching = Task.Run(() => RunAsync(ClientFor("daemon-alpha"), store));

        await AssertEx.CompletesAsync(matching,
            TestBudgets.Contended,
            "A probe whose pin already matches waited for an unrelated probe that was still talking to the daemon: "
            + "the transaction gate is spanning the daemon call again.");

        AssertEx.Equal(DockerDaemonProbeReason.Ready, (await matching).Reason);
        // A match approves nothing, so the seeded pin is still the only one this node ever recorded.
        AssertEx.Equal(expected: 0, store.WriteCount);

        release.TrySetResult();
        AssertEx.Equal(DockerDaemonProbeReason.Ready, (await parked).Reason);

        async Task<DockerDaemonIdentity> ParkAsync()
        {
            entered.TrySetResult();
            await release.Task;

            return ClientFor("daemon-alpha").Identity;
        }
    }

    [Test]
    public async Task AProbeWhosePinMovesWhileItIsTalkingToTheDaemon_ReportsTheSubstitution()
    {
        // The staleness a pre-call compare would hide. This probe reads a pin naming daemon-alpha and then parks
        // inside its transport call; while it is parked an operator confirmation moves the shared pin to
        // daemon-beta. Answering from the pre-call read would report Ready for a daemon this node has stopped
        // trusting, and ContainerRuntimeResolver would cache that approval instead of surfacing the substitution the
        // operator has to answer for. The compare has to happen on a pin read after the daemon answered.
        var store = new InMemoryDaemonAttestationStore();
        store.Seed(Pin("daemon-alpha"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkedClient = Substitute.For<IDockerRuntimeClient>();
        parkedClient.ProbeAsync(Arg.Any<CancellationToken>()).Returns(_ => ParkAsync());

        var parked = Task.Run(() => DockerDaemonProbe.RunAsync(Request(),
            _ => parkedClient,
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger.Instance));

        // The gate is the test's clock: the confirmation runs only once the first probe is provably inside its
        // daemon call, and the first probe is released only once the pin has provably moved.
        await entered.Task;

        var confirmation = await RunAsync(ClientFor("daemon-beta"), store, confirmingDaemonId: "daemon-beta");

        AssertEx.Equal(DockerDaemonProbeReason.Ready, confirmation.Reason);
        AssertEx.Equal("daemon-beta", AssertEx.NotNull(await store.ReadAsync()).DaemonId);

        release.TrySetResult();
        var outcome = await parked;

        AssertEx.Equal(DockerDaemonPreflightStatus.DaemonIdentityChanged, outcome.Status);
        AssertEx.Equal(DockerDaemonProbeReason.IdentityChanged, outcome.Reason);
        // Reported against the pin as it stands now, not the one this probe read before the call.
        AssertEx.Equal("daemon-beta", AssertEx.NotNull(outcome.PinnedDaemon).DaemonId);
        // A refusal approves nothing: the operator's confirmation is still the only write this node has taken.
        AssertEx.Equal(expected: 1, store.WriteCount);
        AssertEx.Equal("daemon-beta", AssertEx.NotNull(await store.ReadAsync()).DaemonId);

        async Task<DockerDaemonIdentity> ParkAsync()
        {
            entered.TrySetResult();
            await release.Task;

            return ClientFor("daemon-alpha").Identity;
        }
    }

    [Test]
    public async Task EveryProbeReason_IsMappedToItsOwnDevelopmentModeMessage()
    {
        // The compiler rejects a switch that omits a named reason (CS8509) but not one that lets a new reason fall
        // into the throwing catch-all CS8524 forces it to carry. So the enumeration is asserted here: a reason added
        // without prose is a red test rather than an exception a user meets first. Every message must also be
        // distinct, which is the property the whole extraction exists to preserve.
        var messages = new Dictionary<DockerDaemonProbeReason, string>();

        foreach (var reason in Enum.GetValues<DockerDaemonProbeReason>())
        {
            var (client, store) = Doubles();
            var confirming = await ArrangeForReasonAsync(reason, client, store);
            var preflight = confirming is null
                ? await Preflight(client, store).InspectAsync()
                : await Preflight(client, store).ConfirmAsync(confirming);

            AssertEx.NotNullOrEmpty(preflight.Message, $"{reason} produced no operator message.");
            messages[reason] = preflight.Message;
        }

        AssertEx.Equal(Enum.GetValues<DockerDaemonProbeReason>().Length, messages.Count);
        AssertEx.Equal(messages.Count,
            messages.Values.Distinct(StringComparer.Ordinal).Count(),
            "Two probe reasons produced the same operator message, so one of them is wearing the other's words.");
    }

    /// <summary>
    ///     Drives <paramref name="client" /> and <paramref name="store" /> into the state that produces
    ///     <paramref name="reason" />, returning the daemon id to confirm with, or null for a plain read.
    /// </summary>
    private static async Task<string?> ArrangeForReasonAsync(DockerDaemonProbeReason reason,
        FakeDockerRuntimeClient client,
        InMemoryDaemonAttestationStore store)
    {
        switch (reason)
        {
            case DockerDaemonProbeReason.Ready:
                return null;

            case DockerDaemonProbeReason.TransportFailure:
                client.ProbeFailure = new DockerRuntimeException(DockerDaemonPreflightStatus.DaemonUnreachable, "nothing answered");
                return null;

            case DockerDaemonProbeReason.SeccompUnsupported:
                client.Identity = client.Identity with
                {
                    SupportsSeccomp = false
                };
                return null;

            case DockerDaemonProbeReason.ApiVersionTooOld:
                client.Identity = client.Identity with
                {
                    ApiVersion = "1.40"
                };
                return null;

            case DockerDaemonProbeReason.IdentityChanged:
                await PinAsync(store, "daemon-alpha");
                client.Identity = client.Identity with
                {
                    DaemonId = "daemon-beta"
                };
                return null;

            case DockerDaemonProbeReason.ConfirmationRaced:
                await PinAsync(store, "daemon-alpha");
                client.Identity = client.Identity with
                {
                    DaemonId = "daemon-gamma"
                };
                return "daemon-beta";

            default:
                throw new InvalidOperationException($"No arrangement for probe reason '{reason}'.");
        }
    }

    private static Task PinAsync(InMemoryDaemonAttestationStore store, string daemonId)
    {
        return store.WriteAsync(Pin(daemonId));
    }

    private static DockerDaemonAttestation Pin(string daemonId)
    {
        return new DockerDaemonAttestation
        {
            DaemonId = daemonId,
            Endpoint = ConfiguredEndpoint,
            EndpointSource = DockerDaemonEndpointSource.Configuration,
            ServerVersion = "99.0.0",
            ConfirmedAtUtc = FixedNow,
            ConfirmedByOperator = false
        };
    }

    private static IDockerDaemonPreflightService Preflight(FakeDockerRuntimeClient client, InMemoryDaemonAttestationStore store)
    {
        var options = DockerSandboxHardeningTests.Options() with
        {
            DaemonEndpoint = ConfiguredEndpoint
        };

        return new DockerDaemonPreflightService(new StaticOptionsMonitor<ContainerSandboxOptions>(options),
            new SingleClientFactory(client),
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerDaemonPreflightService>.Instance);
    }

    private static Task<DockerDaemonProbeOutcome> RunAsync(FakeDockerRuntimeClient client,
        IDockerDaemonAttestationStore store,
        bool requireSeccompSupport = true,
        string? confirmingDaemonId = null,
        DockerDaemonEndpoint? resolvedEndpoint = null)
    {
        return DockerDaemonProbe.RunAsync(Request(requireSeccompSupport, confirmingDaemonId, resolvedEndpoint),
            _ => client,
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger.Instance);
    }

    private static DockerDaemonProbeRequest Request(bool requireSeccompSupport = true,
        string? confirmingDaemonId = null,
        DockerDaemonEndpoint? resolvedEndpoint = null)
    {
        return new DockerDaemonProbeRequest
        {
            ConfiguredEndpoint = ConfiguredEndpoint,
            MinimumApiVersion = "1.41",
            RequireSeccompSupport = requireSeccompSupport,
            ConfirmingDaemonId = confirmingDaemonId,
            ResolvedEndpoint = resolvedEndpoint
        };
    }

    private static (FakeDockerRuntimeClient Client, InMemoryDaemonAttestationStore Store) Doubles()
    {
        var endpoint = new DockerDaemonEndpoint(new Uri(ConfiguredEndpoint), DockerDaemonEndpointSource.Configuration);
        var client = new FakeDockerRuntimeClient(endpoint,
            new DockerDaemonIdentity("daemon-alpha", "99.0.0", "1.99", "1.40", "linux", endpoint, IsRootless: false, SupportsSeccomp: true));

        return (client, new InMemoryDaemonAttestationStore());
    }

    private static FakeDockerRuntimeClient ClientFor(string daemonId)
    {
        var endpoint = new DockerDaemonEndpoint(new Uri(ConfiguredEndpoint), DockerDaemonEndpointSource.Configuration);

        return new FakeDockerRuntimeClient(endpoint,
            new DockerDaemonIdentity(daemonId, "99.0.0", "1.99", "1.40", "linux", endpoint, IsRootless: false, SupportsSeccomp: true));
    }

    /// <summary>
    ///     An attestation store that records the order of its operations and parks inside its FIRST write until the
    ///     test lets it go. The park is what makes the interleaving certain rather than hoped for: while it holds,
    ///     one probe is provably inside its transaction, so a second probe launched then either waits for the gate or
    ///     proves there is none.
    /// </summary>
    private sealed class GatedAttestationStore : IDockerDaemonAttestationStore
    {
        private readonly TaskCompletionSource _writeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _writeReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _events = [];
        private DockerDaemonAttestation? _attestation;

        /// <summary>Completes when a write has begun, so the test knows a transaction is open.</summary>
        public Task WriteEntered => _writeEntered.Task;

        /// <summary>Every operation in the order it was entered, which is where a lost update would show.</summary>
        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public int WriteCount { get; private set; }

        public void ReleaseWrite()
        {
            _writeReleased.TrySetResult();
        }

        public Task<DockerDaemonAttestation?> ReadAsync(CancellationToken cancellationToken = default)
        {
            Record("read");
            return Task.FromResult(_attestation);
        }

        public async Task WriteAsync(DockerDaemonAttestation attestation, CancellationToken cancellationToken = default)
        {
            Record("write");
            _writeEntered.TrySetResult();
            await _writeReleased.Task;

            _attestation = attestation;
            WriteCount++;
        }

        private void Record(string operation)
        {
            lock (_events)
            {
                _events.Add(operation);
            }
        }
    }

    private sealed class SingleClientFactory : IDockerRuntimeClientFactory
    {
        private readonly FakeDockerRuntimeClient _client;

        public SingleClientFactory(FakeDockerRuntimeClient client)
        {
            _client = client;
        }

        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
        {
            return _client;
        }
    }
}
