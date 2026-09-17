namespace XE_Local_AI_Engine.Tests.Containers;

using System.Globalization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Tests.ContainerSandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The one door application containers reach a daemon through: selection, the refusals that happen before any
///     wire call, the preflight-to-runtime status mapping, and the cache.
///     <para>
///         Every case here uses an in-memory attestation store, never the production one: that store is a single
///         unkeyed pin in a file under the node data directory, so a test using it would pin the developer's real
///         daemon and race the Development Mode sandbox suite, which pins the same file.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ContainerRuntimeResolverTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 9, day: 11, hour: 9, minute: 0, second: 0, TimeSpan.Zero);

    private const string LocalEndpoint = "unix:///fake-container-runtime.sock";

    [Test]
    public async Task Auto_ResolvesToDocker()
    {
        var harness = new Harness();

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.Ready, resolution.Status);
        AssertEx.True(resolution.Ready);
        // Docker is the only provider in V1, and Auto must name it rather than leaving the caller to infer one.
        AssertEx.Equal("docker", resolution.Provider);
        AssertEx.Equal(LocalEndpoint, resolution.Daemon.Endpoint);
        AssertEx.Equal(DockerDaemonEndpointSource.Configuration, resolution.Daemon.EndpointSource);
    }

    [Test]
    public async Task AnInstanceOverride_OutranksTheStoredSelection()
    {
        // Both selections resolve to Docker in V1, so "outranks" is only observable as this: an instance that carries
        // its own selection never reads the node setting at all. A resolver that read it anyway and then discarded it
        // would have acquired a persistence dependency on the path that has least reason to have one.
        var harness = new Harness(storedSelection: ContainerRuntimeSelectionParser.Docker);

        var resolution = await harness.Resolver.ResolveAsync(ContainerRuntimeSelection.Auto);

        AssertEx.Equal(ContainerRuntimeStatus.Ready, resolution.Status);
        await harness.SettingsStore.DidNotReceive().LoadAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("kubernetes")]
    [Arguments("")]
    [Arguments("  ")]
    public async Task AnUnparseableStoredSelection_FallsBackToAuto(string storedSelection)
    {
        // The settings file is hand-editable, so an unrecognised value is reachable in production. It must degrade to
        // the default rather than fail: a typo in one field is not a reason for a node to have no container runtime.
        var harness = new Harness(storedSelection);

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.Ready, resolution.Status);
        AssertEx.Equal("docker", resolution.Provider);
    }

    [Test]
    [Arguments(DockerDaemonPreflightStatus.Ready, ContainerRuntimeStatus.Ready)]
    [Arguments(DockerDaemonPreflightStatus.DaemonUnreachable, ContainerRuntimeStatus.DaemonUnreachable)]
    [Arguments(DockerDaemonPreflightStatus.PermissionDenied, ContainerRuntimeStatus.PermissionDenied)]
    [Arguments(DockerDaemonPreflightStatus.ApiVersionTooOld, ContainerRuntimeStatus.ApiVersionTooOld)]
    [Arguments(DockerDaemonPreflightStatus.DaemonIdentityChanged, ContainerRuntimeStatus.DaemonIdentityChanged)]
    [Arguments(DockerDaemonPreflightStatus.NotConfigured, ContainerRuntimeStatus.NotConfigured)]
    [Arguments(DockerDaemonPreflightStatus.ProbeFailed, ContainerRuntimeStatus.ProbeFailed)]
    public void EveryPreflightStatus_MapsToItsRuntimeStatus(DockerDaemonPreflightStatus preflight, ContainerRuntimeStatus runtime)
    {
        // NotConfigured is unreachable in V1 — its producers are Development Mode's own gates and faulty options fail
        // at ValidateOnStart — and is mapped anyway so the switch stays total and the two enums keep their parity.
        AssertEx.Equal(runtime, ContainerRuntimeResolver.ToRuntimeStatus(preflight));
    }

    [Test]
    public void TheTwoStatusEnums_AgreeOnNamesAndValues()
    {
        // The mapping above is one arm per member; this is what makes a member ADDED to the preflight enum visible,
        // since C# cannot reject a switch whose catch-all only throws.
        var preflight = Enum.GetValues<DockerDaemonPreflightStatus>()
                            .Select(static status => (status.ToString(), (int)status))
                            .ToArray();
        var runtime = Enum.GetValues<ContainerRuntimeStatus>()
                          .Select(static status => (status.ToString(), (int)status))
                          .ToArray();

        AssertEx.Equal(preflight.Length, runtime.Length,
            "ContainerRuntimeStatus mirrors DockerDaemonPreflightStatus value for value; one of them gained a member.");
        foreach (var (name, value) in preflight)
        {
            AssertEx.Contains(runtime, entry => entry.Item1 == name && entry.Item2 == value,
                $"ContainerRuntimeStatus has no member '{name}' with value {value}.");
        }
    }

    [Test]
    public async Task ReadyResolution_AdvertisesEveryCapabilityExceptGpuDevices()
    {
        var harness = new Harness();

        var resolution = await harness.Resolver.ResolveAsync();

        var missing = resolution.Capabilities.FindMissing(ContainerRuntimeCapabilities.Names);
        AssertEx.Equal(expected: 1, missing.Count, "A ready Docker daemon offers everything but GPU devices.");
        AssertEx.Equal("gpuDevices", missing[0]);
    }

    [Test]
    [Arguments(DockerDaemonPreflightStatus.Ready)]
    [Arguments(DockerDaemonPreflightStatus.DaemonUnreachable)]
    [Arguments(DockerDaemonPreflightStatus.PermissionDenied)]
    [Arguments(DockerDaemonPreflightStatus.ApiVersionTooOld)]
    [Arguments(DockerDaemonPreflightStatus.DaemonIdentityChanged)]
    [Arguments(DockerDaemonPreflightStatus.NotConfigured)]
    [Arguments(DockerDaemonPreflightStatus.ProbeFailed)]
    public async Task GpuDevices_IsFalseOnEveryStatus(DockerDaemonPreflightStatus status)
    {
        // ADR 0010 makes GPU device requests a non-goal, so the flag exists to refuse a manifest that asks for one BY
        // NAME. A ready daemon advertising it would make that refusal disappear on exactly the machines that have a GPU.
        var resolution = await ResolveForAsync(status);

        AssertEx.False(resolution.Capabilities.GpuDevices);
    }

    [Test]
    [Arguments(DockerDaemonPreflightStatus.Ready, true)]
    [Arguments(DockerDaemonPreflightStatus.PermissionDenied, true)]
    [Arguments(DockerDaemonPreflightStatus.ApiVersionTooOld, true)]
    [Arguments(DockerDaemonPreflightStatus.DaemonIdentityChanged, true)]
    [Arguments(DockerDaemonPreflightStatus.DaemonUnreachable, false)]
    [Arguments(DockerDaemonPreflightStatus.NotConfigured, false)]
    [Arguments(DockerDaemonPreflightStatus.ProbeFailed, false)]
    public async Task Available_IsFalseOnlyForDaemonUnreachableNotConfiguredAndProbeFailed(DockerDaemonPreflightStatus status,
        bool available)
    {
        // Available and Ready disagree on exactly PermissionDenied, ApiVersionTooOld and DaemonIdentityChanged: a
        // daemon answered and the operator has an action to take. A caller reading Ready for "is there a container
        // runtime on this machine" tells that operator there is none.
        var resolution = await ResolveForAsync(status);

        AssertEx.Equal(available, resolution.Available);
        AssertEx.Equal(status == DockerDaemonPreflightStatus.Ready, resolution.Ready);
        AssertEx.Equal(status == DockerDaemonPreflightStatus.DaemonIdentityChanged, resolution.RequiresOperatorConfirmation);
    }

    [Test]
    [Arguments("windows")]
    [Arguments("Windows")]
    public async Task ANonLinuxDaemon_IsProbeFailedAndTheMessageNamesTheOperatingSystem(string operatingSystem)
    {
        // A Windows-container daemon answers every question the probe asks and means something different by every
        // answer: a bind mount, a loopback published port and a Linux image reference are not the same objects there.
        var harness = new Harness();
        harness.Client.Identity = harness.Client.Identity with
        {
            OperatingSystem = operatingSystem
        };

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.ProbeFailed, resolution.Status);
        AssertEx.Equal(ContainerRuntimeCapabilities.None, resolution.Capabilities);
        AssertEx.Contains(resolution.Message, operatingSystem, StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task ALinuxDaemonReportedInAnyCasing_IsReady()
    {
        var harness = new Harness();
        harness.Client.Identity = harness.Client.Identity with
        {
            OperatingSystem = "Linux"
        };

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.Ready, resolution.Status);
    }

    [Test]
    [Arguments("tcp://10.0.0.4:2375", DockerDaemonEndpointSource.Configuration)]
    [Arguments("http://10.0.0.4:2375", DockerDaemonEndpointSource.Configuration)]
    [Arguments("https://10.0.0.4:2376", DockerDaemonEndpointSource.Configuration)]
    [Arguments("tcp://10.0.0.4:2375", DockerDaemonEndpointSource.DockerHostEnvironmentVariable)]
    [Arguments("http://10.0.0.4:2375", DockerDaemonEndpointSource.DockerHostEnvironmentVariable)]
    [Arguments("https://10.0.0.4:2376", DockerDaemonEndpointSource.DockerHostEnvironmentVariable)]
    public async Task ARemoteDaemonEndpoint_IsProbeFailedAndNoClientIsEverCreated(string endpoint,
        DockerDaemonEndpointSource source)
    {
        // The refusal must happen before a client exists, not merely before a container is created: nothing about this
        // node — not a socket path, not a label, not a probe — may be transmitted to a daemon on another host. Both
        // sources are covered because DOCKER_HOST is the one an operator sets without touching engine configuration.
        var harness = new Harness(endpoint: new DockerDaemonEndpoint(new Uri(endpoint), source));

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.ProbeFailed, resolution.Status);
        AssertEx.False(resolution.Available);
        AssertEx.Contains(resolution.Message, "Remote daemons are not supported");
        AssertEx.Contains(resolution.Message, endpoint, StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(source, resolution.Daemon.EndpointSource);
        AssertEx.Equal(expected: 0, harness.Factory.CreateRuntimeCallCount,
            "A client was constructed for a remote daemon, so the refusal came too late to have transmitted nothing.");
        AssertEx.Null(resolution.Daemon.DaemonId);
    }

    [Test]
    [Arguments("tcp://someone:{0}@10.0.0.4:2375", "user information")]
    [Arguments("http://{0}@registry.example:2375", "user information")]
    [Arguments("tcp://10.0.0.4:2375/?token={0}", "a query string")]
    [Arguments("tcp://10.0.0.4:2375/#{0}", "a fragment")]
    [Arguments("unix:///var/run/docker.sock?token={0}", "a query string")]
    public async Task AnEndpointCarryingADisclosingComponent_IsRefusedWithoutEchoingIt(string template, string component)
    {
        // DOCKER_HOST is an environment variable, and an operator who puts a secret in one has put a live value where
        // this engine renders endpoints: a log line, a resolution record an API returns, an exception message, and the
        // 503 body those become. Refusing the endpoint and then printing it would disclose the secret in the course of
        // declining to use it, so the refusal names where the value came from and which component held it.
        //
        // The unix:// case is the one no other check catches: a local socket passes the transport check, so without
        // this refusal a query string on it would reach a READY resolution and be rendered on the runtime card.
        const string Sentinel = "sekrit-9f3a";
        var raw = string.Format(CultureInfo.InvariantCulture, template, Sentinel);
        var harness = new Harness(endpoint: new DockerDaemonEndpoint(new Uri(raw), DockerDaemonEndpointSource.DockerHostEnvironmentVariable));

        var resolution = await harness.Resolver.ResolveAsync();
        var failure = await AssertEx.ThrowsAsync<ContainerRuntimeUnavailableException>(() => harness.Resolver.CreateRuntimeAsync());

        AssertEx.Equal(ContainerRuntimeStatus.ProbeFailed, resolution.Status);
        AssertEx.False(resolution.Available);
        AssertEx.Equal(expected: 0, harness.Factory.CreateRuntimeCallCount,
            "A client was constructed for an endpoint carrying credentials, so the secret reached the transport layer.");

        AssertEx.False(resolution.Message.Contains(Sentinel, StringComparison.Ordinal),
            "The refusal message repeated the credential back.");
        AssertEx.False(resolution.Daemon.Endpoint.Contains(Sentinel, StringComparison.Ordinal),
            "The resolution's endpoint field carries the credential, and that field is returned by an API.");
        AssertEx.False(failure.Message.Contains(Sentinel, StringComparison.Ordinal),
            "The exception a caller of CreateRuntimeAsync sees carries the credential.");
        AssertEx.Empty(harness.Logger.Entries.Where(entry => entry.Message.Contains(Sentinel, StringComparison.Ordinal)),
            "The credential reached the log.");

        // Refused for carrying a secret, which is a different fix from "point this at a local socket" — and the
        // message names the component so the operator knows which part of the value to remove.
        AssertEx.Contains(resolution.Message, component);
        AssertEx.False(resolution.Daemon.Endpoint.Contains('?', StringComparison.Ordinal),
            $"The rendered endpoint '{resolution.Daemon.Endpoint}' still carries a query string.");
        AssertEx.False(resolution.Daemon.Endpoint.Contains('#', StringComparison.Ordinal),
            $"The rendered endpoint '{resolution.Daemon.Endpoint}' still carries a fragment.");
    }

    [Test]
    [Arguments("unix:///var/run/docker.sock", DockerDaemonEndpointSource.DefaultUnixSocket)]
    [Arguments("npipe://./pipe/docker_engine", DockerDaemonEndpointSource.WindowsNamedPipe)]
    public async Task AUnixOrNamedPipeEndpoint_IsAccepted(string endpoint, DockerDaemonEndpointSource source)
    {
        var harness = new Harness(endpoint: new DockerDaemonEndpoint(new Uri(endpoint), source));

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.Ready, resolution.Status);
        AssertEx.Equal(expected: 1, harness.Factory.CreateRuntimeCallCount);
        AssertEx.Equal(source, resolution.Daemon.EndpointSource);
    }

    [Test]
    public async Task AResolutionWithinTheCacheWindow_DoesNotTouchTheDaemon()
    {
        var harness = new Harness();

        await harness.Resolver.ResolveAsync();
        var second = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.Ready, second.Status);
        AssertEx.Equal(expected: 1, harness.Factory.CreateRuntimeCallCount,
            "The second resolution probed the daemon inside its own cache window.");
    }

    [Test]
    public async Task ForceRefresh_AlwaysProbes()
    {
        var harness = new Harness();

        await harness.Resolver.ResolveAsync();
        await harness.Resolver.ResolveAsync(forceRefresh: true);

        AssertEx.Equal(expected: 2, harness.Factory.CreateRuntimeCallCount);
    }

    [Test]
    public async Task AZeroCacheWindow_ProbesEveryTime()
    {
        // Zero is an accepted operator choice rather than a validation failure, so it has to mean something.
        var harness = new Harness(resolutionCacheSeconds: 0);

        await harness.Resolver.ResolveAsync();
        await harness.Resolver.ResolveAsync();

        AssertEx.Equal(expected: 2, harness.Factory.CreateRuntimeCallCount);
    }

    [Test]
    public async Task ConfirmDaemonIdentity_AlwaysProbesAndReplacesTheCache()
    {
        // A confirmation applied to a cached observation would approve whichever daemon is there now rather than the
        // one the operator was shown, which is worse than having no confirmation step because it looks like one.
        var harness = new Harness();
        harness.Client.Identity = harness.Client.Identity with
        {
            DaemonId = "daemon-alpha"
        };
        await harness.Resolver.ResolveAsync();

        harness.Client.Identity = harness.Client.Identity with
        {
            DaemonId = "daemon-beta"
        };
        var changed = await harness.Resolver.ResolveAsync(forceRefresh: true);
        AssertEx.Equal(ContainerRuntimeStatus.DaemonIdentityChanged, changed.Status);

        var confirmed = await harness.Resolver.ConfirmDaemonIdentityAsync("daemon-beta");

        AssertEx.Equal(ContainerRuntimeStatus.Ready, confirmed.Status);
        AssertEx.Equal("daemon-beta", confirmed.Daemon.PinnedDaemonId);
        AssertEx.True(AssertEx.NotNull(await harness.AttestationStore.ReadAsync()).ConfirmedByOperator);

        // Replaced, not merely written: the next read must not serve the pre-confirmation resolution back.
        var afterwards = await harness.Resolver.ResolveAsync();
        AssertEx.Equal(ContainerRuntimeStatus.Ready, afterwards.Status);
    }

    [Test]
    public async Task ConfirmDaemonIdentity_ForADaemonThatMovedAgain_ApprovesNothing()
    {
        var harness = new Harness();
        harness.Client.Identity = harness.Client.Identity with
        {
            DaemonId = "daemon-alpha"
        };
        await harness.Resolver.ResolveAsync();
        harness.Client.Identity = harness.Client.Identity with
        {
            DaemonId = "daemon-gamma"
        };

        var resolution = await harness.Resolver.ConfirmDaemonIdentityAsync("daemon-beta");

        AssertEx.Equal(ContainerRuntimeStatus.DaemonIdentityChanged, resolution.Status);
        AssertEx.Equal("daemon-alpha", AssertEx.NotNull(await harness.AttestationStore.ReadAsync()).DaemonId);
    }

    [Test]
    [Arguments(DockerDaemonPreflightStatus.DaemonUnreachable)]
    [Arguments(DockerDaemonPreflightStatus.PermissionDenied)]
    [Arguments(DockerDaemonPreflightStatus.ApiVersionTooOld)]
    [Arguments(DockerDaemonPreflightStatus.DaemonIdentityChanged)]
    [Arguments(DockerDaemonPreflightStatus.NotConfigured)]
    [Arguments(DockerDaemonPreflightStatus.ProbeFailed)]
    public async Task CreateRuntime_OnANonReadyStatus_ThrowsCarryingTheResolution(DockerDaemonPreflightStatus status)
    {
        // There is no degraded mode: a container created against a daemon that failed its preflight is one whose
        // confinement was never established. The resolution rides along so the caller reports the operator the same
        // status and prose the runtime card shows rather than a second description written at the catch site.
        var harness = HarnessFor(status);

        var exception = await AssertEx.ThrowsAsync<ContainerRuntimeUnavailableException>(() => harness.Resolver.CreateRuntimeAsync());

        var resolution = AssertEx.NotNull(exception.Resolution);
        AssertEx.Equal(ContainerRuntimeResolver.ToRuntimeStatus(status), resolution.Status);
        AssertEx.False(resolution.Ready);
        AssertEx.Equal(resolution.Message, exception.Message);
        AssertEx.NotNullOrEmpty(resolution.Message);
    }

    [Test]
    public async Task CreateRuntime_OnAReadyResolution_BuildsAClientForTheResolvedEndpoint()
    {
        var harness = new Harness();

        var runtime = await harness.Resolver.CreateRuntimeAsync();

        AssertEx.NotNull(runtime);
        // One create for the probe, one for the caller. The second must have been asked for the same endpoint the
        // resolution reported, or the runtime the caller holds is not the daemon the resolution approved.
        AssertEx.Equal(expected: 2, harness.Factory.CreateRuntimeCallCount);
        AssertEx.Equal(LocalEndpoint, harness.Factory.RequestedEndpoints[^1].Display);
    }

    [Test]
    public async Task EveryStatus_ProducesItsOwnOperatorProse()
    {
        // The reason the shared probe writes none. ADR 0004 makes Development Mode's messages the entire experience of
        // a missing daemon for that feature; a user whose installed application will not start needs different words
        // for the identical status, and two statuses sharing one message is one of them wearing the other's.
        var messages = new List<string>();

        foreach (var status in Enum.GetValues<DockerDaemonPreflightStatus>())
        {
            var resolution = await ResolveForAsync(status);
            AssertEx.NotNullOrEmpty(resolution.Message, $"{status} produced no operator message.");
            messages.Add(resolution.Message);
        }

        AssertEx.Equal(messages.Count,
            messages.Distinct(StringComparer.Ordinal).Count(),
            "Two runtime statuses produced the same operator message, so one of them is wearing the other's words.");
    }

    [Test]
    public async Task TheFirstUsePin_CarriesTheSourceThisResolverObserved()
    {
        // The resolver settles the endpoint and hands it to the shared probe. If it restated the result as a string
        // the probe would re-run discovery, read it back as an explicit setting, and write Configuration into the
        // pin — the pin an operator is later shown when the daemon that answers is not the one this node approved.
        var harness = new Harness(endpoint: new DockerDaemonEndpoint(new Uri(LocalEndpoint),
            DockerDaemonEndpointSource.DockerHostEnvironmentVariable));

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.Ready, resolution.Status);
        AssertEx.Equal(DockerDaemonEndpointSource.DockerHostEnvironmentVariable, resolution.Daemon.EndpointSource);
        var pinned = AssertEx.NotNull(await harness.AttestationStore.ReadAsync());
        AssertEx.Equal(DockerDaemonEndpointSource.DockerHostEnvironmentVariable, pinned.EndpointSource);
    }

    [Test]
    public async Task AnUnavailableRuntime_IsLoggedWithItsEndpointAndStatus()
    {
        // Without this line a node whose installed applications will not start says nothing whatever in its log until
        // somebody opens the UI, because every word this resolver writes goes to the caller that asked.
        var harness = HarnessFor(DockerDaemonPreflightStatus.DaemonUnreachable);

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.Equal(ContainerRuntimeStatus.DaemonUnreachable, resolution.Status);
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Information, LocalEndpoint),
            "The unavailable resolution was not logged with the endpoint it was resolved against.");
        AssertEx.True(harness.Logger.HasEntry(LogLevel.Information, nameof(ContainerRuntimeStatus.DaemonUnreachable)),
            "The unavailable resolution was logged without the status that says why.");
    }

    [Test]
    public async Task AReadyRuntime_IsNotLoggedAsUnavailable()
    {
        // Only when something is wrong: a node that reaches its daemon on every resolution must not narrate the
        // cache-miss path into the log every few seconds. The shared probe's own first-use pin line still appears,
        // which is why this asserts the absence of the unavailable line rather than of every line.
        var harness = new Harness();

        var resolution = await harness.Resolver.ResolveAsync();

        AssertEx.True(resolution.Ready);
        AssertEx.False(harness.Logger.HasEntry(LogLevel.Information, "unavailable"),
            "A ready runtime was logged as unavailable.");
    }

    /// <summary>Resolves once against a harness driven into <paramref name="status" />.</summary>
    private static async Task<ContainerRuntimeResolution> ResolveForAsync(DockerDaemonPreflightStatus status)
    {
        var harness = HarnessFor(status);
        return await harness.Resolver.ResolveAsync();
    }

    /// <summary>Builds a harness whose daemon produces <paramref name="status" /> on the next probe.</summary>
    private static Harness HarnessFor(DockerDaemonPreflightStatus status)
    {
        var harness = new Harness();

        switch (status)
        {
            case DockerDaemonPreflightStatus.Ready:
                break;

            case DockerDaemonPreflightStatus.ApiVersionTooOld:
                // 1.9 precedes 1.41; read as a decimal it would pass the gate.
                harness.Client.Identity = harness.Client.Identity with
                {
                    ApiVersion = "1.9"
                };
                break;

            case DockerDaemonPreflightStatus.DaemonIdentityChanged:
                harness.AttestationStore.Seed(new DockerDaemonAttestation
                {
                    DaemonId = "daemon-previously-approved",
                    Endpoint = LocalEndpoint,
                    EndpointSource = DockerDaemonEndpointSource.Configuration,
                    ServerVersion = "98.0.0",
                    ConfirmedAtUtc = FixedNow - TimeSpan.FromDays(3),
                    ConfirmedByOperator = true
                });
                break;

            default:
                harness.Client.ProbeFailure = new DockerRuntimeException(status, $"the daemon reported {status}.");
                break;
        }

        return harness;
    }

    /// <summary>
    ///     A resolver over a fake daemon, an in-memory pin and a substituted settings store, with the endpoint supplied
    ///     rather than discovered so that <c>DOCKER_HOST</c> can be exercised without mutating the environment every
    ///     other test in this run shares.
    /// </summary>
    private sealed class Harness
    {
        public Harness(string? storedSelection = null,
            DockerDaemonEndpoint? endpoint = null,
            int resolutionCacheSeconds = 30)
        {
            Endpoint = endpoint ?? new DockerDaemonEndpoint(new Uri(LocalEndpoint), DockerDaemonEndpointSource.Configuration);
            Client = new FakeDockerRuntimeClient(Endpoint,
                new DockerDaemonIdentity("daemon-alpha", "99.0.0", "1.99", "1.40", "linux", Endpoint, IsRootless: true, SupportsSeccomp: true));
            Factory = new RecordingContainerRuntimeFactory(Client);
            AttestationStore = new InMemoryDaemonAttestationStore();

            SettingsStore = Substitute.For<INodeSettingsStore>();
            SettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult(new StoredNodeSettings
                         {
                             ContainerRuntimeSelection = storedSelection
                         }));

            Resolver = new ContainerRuntimeResolver(SettingsStore,
                new StaticOptionsMonitor<ContainerRuntimeOptions>(new ContainerRuntimeOptions
                {
                    ResolutionCacheSeconds = resolutionCacheSeconds
                }),
                Factory,
                AttestationStore,
                new FixedTimeProvider(FixedNow),
                Logger,
                _ => Endpoint);
        }

        public InMemoryDaemonAttestationStore AttestationStore { get; }

        public RecordingLogger<ContainerRuntimeResolver> Logger { get; } = new();

        public FakeDockerRuntimeClient Client { get; }

        public DockerDaemonEndpoint Endpoint { get; }

        public RecordingContainerRuntimeFactory Factory { get; }

        public ContainerRuntimeResolver Resolver { get; }

        public INodeSettingsStore SettingsStore { get; }
    }

    /// <summary>
    ///     Records every endpoint a client was asked for. The count is what proves a remote endpoint was refused before
    ///     anything was constructed, and what tells a cached resolution apart from a fresh probe.
    /// </summary>
    private sealed class RecordingContainerRuntimeFactory : IContainerRuntimeFactory
    {
        private readonly FakeDockerRuntimeClient _client;
        private readonly List<DockerDaemonEndpoint> _requested = [];

        public RecordingContainerRuntimeFactory(FakeDockerRuntimeClient client)
        {
            _client = client;
        }

        public int CreateRuntimeCallCount => _requested.Count;

        public IReadOnlyList<DockerDaemonEndpoint> RequestedEndpoints => _requested;

        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
        {
            return CreateRuntime(endpoint);
        }

        public IContainerRuntime CreateRuntime(DockerDaemonEndpoint endpoint)
        {
            _requested.Add(endpoint);
            return _client;
        }
    }
}
