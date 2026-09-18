namespace XE_Local_AI_Engine.Tests.Containers;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.ContainerSandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The only proof that a real container on an engine-owned network reaches the bridge.
///     <para>
///         Everything the unit tests establish about the bridge is established against a socket this process opened
///         and connected to itself. The claim the feature actually rests on is different and cannot be faked: that a
///         container, whose network namespace is not the host's and which under a rootless daemon cannot see the
///         host's loopback at all, can reach the address <c>ContainerBridgeEndpointResolver</c> chose, and that the
///         same-host peer guard accepts the address such a container arrives from.
///     </para>
///     <para>
///         A sibling of <c>ContainerRuntimeRealDaemonTests</c> rather than an addition to it, with the same skip
///         convention: that class covers the runtime client's contract, this one covers bridge networking, and the
///         two are worth failing independently.
///     </para>
/// </summary>
[Category(TestCategories.ExternalInfra)]
public sealed class ContainerBridgeRealDaemonTests
{
    private const string RequireDockerVariable = "XE_REQUIRE_DOCKER_TESTS";

    private const string ModelName = "bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M";

    private static readonly TimeSpan DaemonDeadline = TimeSpan.FromSeconds(60);

    /// <summary>One probe for the whole class; every test needs the same answer and it cannot differ between them.</summary>
    private static readonly Lazy<Task<ContainerRuntimeOptions>> DaemonGate = new(ResolveUsableDaemonAsync);

    /// <summary>
    ///     The whole feature in one assertion: a container holding its instance's token fetches the node's own model
    ///     list through the bridge, and the body arrives intact.
    /// </summary>
    [Test]
    public async Task RealDaemon_AContainerWithItsToken_ReachesTheNodesModelSurfaceThroughTheBridge()
    {
        await using var box = await NewBoxAsync();

        var exitCode = await box.FetchModelsAsync(box.ValidToken);

        AssertEx.Equal(expected: 0L, exitCode,
            $"A container on an engine-owned network could not reach the bridge at {box.ContainerFacingEndpoint}. "
            + "This is the path an installed application uses to call this node's local model; nothing else proves it. "
            + $"The container said: {await box.ReadFetchedBodyAsync()}");
        AssertEx.Contains(await box.ReadFetchedBodyAsync(), ModelName,
            message: "The model list must round-trip to the container unchanged.");
    }

    /// <summary>
    ///     The peer guard admits the container, and the token gate is then what decides. Without this, a green run
    ///     above would be consistent with a bridge that admits every container on the network unconditionally.
    /// </summary>
    [Test]
    public async Task RealDaemon_AContainerWithoutAToken_IsRefusedByTheBridge()
    {
        await using var box = await NewBoxAsync();

        var exitCode = await box.FetchModelsAsync(token: null);

        AssertEx.NotEqual(notExpected: 0L, exitCode, "A container presenting no token must not be served.");
        AssertEx.False((await box.ReadFetchedBodyAsync()).Contains(ModelName, StringComparison.Ordinal),
            "A refused container must not receive the model list.");
    }

    /// <summary>
    ///     One instance's token must not serve another's. The verifier is substituted here — its own coverage is in
    ///     ExternalAppBridgeTokenVerifierTests, over the real encrypted store — so what this adds is that the refusal
    ///     survives the whole real path: container, network, listener, peer guard, token gate.
    /// </summary>
    [Test]
    public async Task RealDaemon_AContainerWithAnotherInstancesToken_IsRefusedByTheBridge()
    {
        await using var box = await NewBoxAsync();

        var exitCode = await box.FetchModelsAsync(ContainerBridgeToken.Mint(Guid.NewGuid()));

        AssertEx.NotEqual(notExpected: 0L, exitCode, "A token this node never issued must not be served.");
    }

    private static async Task<BridgeBox> NewBoxAsync()
    {
        RequireOptIn();
        var options = await DaemonGate.Value;
        return await BridgeBox.StartAsync(options);
    }

    private static async Task<ContainerRuntimeOptions> ResolveUsableDaemonAsync()
    {
        var options = new ContainerRuntimeOptions();
        var endpoint = DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint);

        await using (var client = BridgeBox.CreateRuntime(options))
        {
            DockerDaemonIdentity identity;
            try
            {
                identity = await client.ProbeAsync();
            }
            catch (DockerRuntimeException exception)
            {
                throw Unavailable($"no usable Docker daemon at '{endpoint.Display}' (found via {endpoint.Source}): {exception.Message}");
            }

            if (!identity.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase))
            {
                throw Unavailable($"the daemon at '{endpoint.Display}' runs '{identity.OperatingSystem}' containers, not Linux. "
                                  + "A container's reach back to the host means something different there.");
            }

            if (!identity.IsRootless)
            {
                throw Rootful(endpoint.Display);
            }

            if (!await client.ImageExistsAsync(ContainerRuntimeTestImages.Busybox))
            {
                await client.PullImageAsync(ContainerRuntimeTestImages.Busybox, progress: null);
            }
        }

        return options;
    }

    /// <summary>
    ///     Why a rootful daemon skips the WHOLE class rather than only the container that has to arrive from an
    ///     address this host owns.
    ///     <para>
    ///         Under rootless Docker, RootlessKit translates a container's traffic and it reaches the bridge from one
    ///         of the host's own addresses, which the peer guard admits; under a rootful daemon a packet whose
    ///         destination is one of this host's own addresses is routed PREROUTING to INPUT without passing
    ///         POSTROUTING, so <c>MASQUERADE</c> never applies, the source address stays the container's own
    ///         <c>172.x.y.z</c>, and the peer guard answers 403. ADR 0011 records that as a deferred limitation.
    ///     </para>
    ///     <para>
    ///         The positive test can only fail there, which is reason enough to skip it. The two negatives look
    ///         daemon-agnostic — a container without a token, and one with another instance's, must be refused — but
    ///         on a rootful daemon they are refused by the PEER GUARD, before the token gate is ever consulted. They
    ///         would then stay green with the token gate deleted, which is a test that has verified nothing. A run
    ///         that cannot distinguish the two refusals is worth less than a skip that says so.
    ///     </para>
    ///     <para>
    ///         A skip even under <c>XE_REQUIRE_DOCKER_TESTS=1</c>, unlike every other refusal in this class: those
    ///         name a prerequisite an operator can install, and this names a known limitation of the feature.
    ///     </para>
    /// </summary>
    private static SkipTestException Rootful(string endpoint)
    {
        return new SkipTestException($"SKIPPED — the daemon at '{endpoint}' is ROOTFUL, and the bridge is expected to be dark there: a container's "
                                     + "traffic to one of this host's own addresses keeps the container's source address, so the same-host peer guard "
                                     + "refuses it before the token gate. That is the deferred limitation in ADR 0011 (admit the subnets of engine-owned "
                                     + "networks), not a defect this run could find. Nothing about the bridge is proven on this box; run these against a "
                                     + "rootless daemon.");
    }

    /// <summary>
    ///     The one switch, shared with the other real-daemon suites: set <c>XE_REQUIRE_DOCKER_TESTS=1</c> and these
    ///     tests run, failing rather than skipping when no daemon is usable; leave it unset and they skip with a
    ///     reason naming the runner. CI never sets it; <c>scripts/run-docker-smoke-local.sh</c> does.
    /// </summary>
    private static void RequireOptIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RequireDockerVariable), "1", StringComparison.Ordinal))
        {
            throw new SkipTestException($"SKIPPED — opt-in: set {RequireDockerVariable}=1 (scripts/run-docker-smoke-local.sh) to run "
                                        + "the real-daemon proof that a container reaches the container bridge. Nothing else proves it.");
        }
    }

    /// <summary>
    ///     Always a failure, never a skip: <see cref="RequireOptIn" /> has already turned away a run that did not
    ///     ask for a daemon, so reaching here means one was PROMISED and is not usable.
    /// </summary>
    private static Exception Unavailable(string reason)
    {
        var message = reason + " This is the ONLY test that proves a real container reaches the container bridge; "
                             + "a green run without it is not evidence that an installed application can call this node's model.";

        return new InvalidOperationException($"REQUIRED — {RequireDockerVariable}=1, so this is a failure rather than a skip: {message}");
    }

    /// <summary>
    ///     A real bridge listener on the address this host's resolver chose, a real engine-owned network, and the
    ///     BusyBox containers that fetch through it.
    /// </summary>
    private sealed class BridgeBox : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<string> _containers = [];
        private readonly ContainerRuntimeOptions _options;
        private readonly ContainerBridgeAddressWatcher _watcher;
        private string? _lastContainerId;
        private int _nameCounter;
        private string? _networkId;

        private BridgeBox(ContainerRuntimeOptions options,
            IContainerRuntime runtime,
            WebApplication app,
            ContainerBridgeAddressWatcher watcher,
            string containerFacingEndpoint,
            string validToken)
        {
            _options = options;
            _app = app;
            _watcher = watcher;
            Runtime = runtime;
            ContainerFacingEndpoint = containerFacingEndpoint;
            ValidToken = validToken;
            RunId = Guid.NewGuid().ToString("N")[..12];
            NetworkName = "xe-bridge-" + RunId;
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["xe.test.suite"] = "container-bridge",
                ["xe.test.run"] = RunId
            };
        }

        public string ContainerFacingEndpoint { get; }

        public IReadOnlyDictionary<string, string> Labels { get; }

        public string NetworkName { get; }

        public string RunId { get; }

        public IContainerRuntime Runtime { get; }

        public string ValidToken { get; }

        public static IContainerRuntime CreateRuntime(ContainerRuntimeOptions options)
        {
            return new DockerContainerRuntimeFactory(new StaticOptionsMonitor<ContainerRuntimeOptions>(options), NullLoggerFactory.Instance, TimeProvider.System)
                .CreateRuntime(DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint));
        }

        public static async Task<BridgeBox> StartAsync(ContainerRuntimeOptions options)
        {
            // The PRODUCTION resolver picks the address, so what the container is told here is what a real node
            // would tell it. A host with no qualifying interface has no bridge and nothing to prove.
            var port = ReservePort();
            var bridgeOptions = new ContainerBridgeOptions
            {
                Enabled = true,
                Port = port
            };
            var endpoint = ContainerBridgeEndpointResolver.Resolve(bridgeOptions, ContainerBridgeEndpointResolver.HostRunsDockerDesktop())
                           ?? throw Unavailable("this host has no up, non-loopback, IPv4 interface, so it could not open a bridge to reach.");

            var instanceId = Guid.NewGuid();
            var validToken = ContainerBridgeToken.Mint(instanceId);
            var typed = Options.Create(bridgeOptions);

            var builder = WebApplication.CreateSlimBuilder();
#pragma warning disable S5332 // The bridge is plain HTTP by design; its peer guard and token gate are the controls.
            builder.WebHost.UseKestrel().UseUrls($"http://{endpoint.BindAddress}:{port}");
#pragma warning restore S5332

            // The PRODUCTION call, not a test shortcut: on a real node host filtering refuses the bridge's own Host
            // header before any middleware runs, so a node without this answers every container 400. It proves
            // nothing HERE — CreateSlimBuilder installs no host filter and this project ships no appsettings.json —
            // and it is in place so this fixture builds the host the way the node does.
            // ContainerBridgeHostFilteringTests is where the widening is actually exercised, against a real filter.
            ContainerBridgePipeline.AllowBridgeHost(builder, endpoint);
            builder.Services.AddSingleton(typed);
            builder.Services.AddSingleton(new ContainerBridgeEndpointSource(endpoint));

            // The REAL watcher over the REAL interface enumeration: whether a container's source address counts as
            // this computer is the single fact this whole class exists to establish.
            var watcher = new ContainerBridgeAddressWatcher(typed, TimeProvider.System, NullLogger<ContainerBridgeAddressWatcher>.Instance);
            builder.Services.AddSingleton(watcher);
            builder.Services.AddSingleton<ContainerBridgePeerGuardMiddleware>();

            var verifier = Substitute.For<IContainerBridgeTokenVerifier>();
            _ = verifier.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((ContainerBridgeCaller?)null);
            _ = verifier.VerifyAsync(validToken, Arg.Any<CancellationToken>()).Returns(new ContainerBridgeCaller(instanceId));
            builder.Services.AddSingleton(verifier);
            builder.Services.AddScoped<ContainerBridgeTokenMiddleware>();

            var models = Substitute.For<IGgufModelStore>();
            _ = models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([
                new LocalModelDescriptor
                {
                    ModelName = ModelName,
                    ProviderName = "llama-server",
                    IsAvailable = true,
                    SizeBytes = 512_000_000,
                    ModifiedAt = DateTimeOffset.UnixEpoch,
                    MaxContextTokens = 32_768
                }
            ]));
            builder.Services.AddSingleton(models);
            builder.Services.AddSingleton(SubstitutedRuntimeOrchestration.Over(Substitute.For<ILlamaServerProcessSupervisor>()));
            builder.Services.AddHttpClient(LocalModelProxyForwarder.HttpClientName);
            builder.Services.AddScoped<LocalModelProxyForwarder>();

            var app = builder.Build();
            ContainerBridgePipeline.Map(app, endpoint);
            await app.StartAsync();

            return new BridgeBox(options, CreateRuntime(options), app, watcher, endpoint.ContainerFacingEndpoint, validToken);
        }

        /// <summary>Runs one BusyBox container that wgets the bridge and exits; returns its exit code.</summary>
        public async Task<long> FetchModelsAsync(string? token)
        {
            var header = token is null
                ? string.Empty
                : $" --header 'Authorization: Bearer {token}'";
            var url = $"http://{ContainerFacingEndpoint}{ContainerBridgePipeline.ModelsPath}";

            await CreateNetworkAsync();
            var containerId = await Runtime.RunContainerAsync(Specification($"wget -q -O -{header} {url}"));
            _containers.Add(containerId);
            _lastContainerId = containerId;

            await Runtime.StartContainerAsync(containerId);

            var finished = await PollAsync(async () => (await Runtime.InspectAsync(containerId)).State,
                    static state => !state.Running,
                    DaemonDeadline,
                    "the fetching container to exit");

            return finished.ExitCode;
        }

        /// <summary>Whatever the last fetching container wrote to stdout — the response body, when it got one.</summary>
        public async Task<string> ReadFetchedBodyAsync()
        {
            var containerId = _lastContainerId ?? throw new InvalidOperationException("Nothing has fetched yet.");
            var logs = await Runtime.ReadLogsAsync(containerId, new ContainerLogRequest
            {
                TailLines = 200
            });
            return logs.Text;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var containerId in _containers)
            {
                try
                {
                    await Runtime.RemoveContainerAsync(containerId);
                }
                catch (DockerRuntimeException)
                {
                    // Cleanup is best effort: a container the daemon already dropped is the outcome this wanted.
                }
            }

            if (_networkId is not null)
            {
                try
                {
                    await Runtime.RemoveNetworkAsync(_networkId);
                }
                catch (DockerRuntimeException)
                {
                    // Same: the network going away by another route is the outcome this wanted.
                }
            }

            await Runtime.DisposeAsync();
            _watcher.Dispose();
            await _app.DisposeAsync();
        }

        // Bind :0, read what the kernel handed out, release it, and bind that number for real — the same window
        // ContainerBridgePipelineTests.ReserveLoopbackPort documents, and for the same reason: Kestrel must know the
        // port before the branch predicate can be built, and a dynamic port cannot be known that early.
        private static int ReservePort()
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Any, port: 0));
            return ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        private static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> reached, TimeSpan timeout, string what)
        {
            // real-timer: the daemon decides when a process exits. A fake clock moves this test's time and nothing
            // the daemon does, so there is nothing here that could be faked.
            using var deadline = new CancellationTokenSource(timeout);
            while (true)
            {
                var last = await read();
                if (reached(last))
                {
                    return last;
                }

                if (deadline.IsCancellationRequested)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                        $"Timed out after {timeout.TotalSeconds:0}s waiting for {what}."));
                }

                // real-timer: polls the real Docker daemon, which offers no readiness signal; the deadline token bounds it.
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
            }
        }

        private async Task CreateNetworkAsync()
        {
            _networkId ??= await Runtime.CreateNetworkAsync(new ContainerNetworkSpecification
            {
                Name = NetworkName,
                Labels = Labels,
                Internal = false
            });
        }

        private ContainerSpecification Specification(string shellCommand)
        {
            return new ContainerSpecification
            {
                Image = ContainerRuntimeTestImages.Busybox,
                Name = string.Create(CultureInfo.InvariantCulture, $"xe-bridge-{RunId}-{++_nameCounter}"),
                User = null,
                Labels = Labels,
                Environment = new Dictionary<string, string>(StringComparer.Ordinal),
                Mounts = [],
                PublishedPorts = [],
                CapabilitiesToDrop = ["ALL"],
                CapabilitiesToAdd = [],
                SecurityOptions = ["no-new-privileges:true", DockerSeccompProfile.SecurityOption],
                ReadOnlyRootFilesystem = true,
                NetworkName = NetworkName,
                NetworkAliases = [],
                RestartMode = ContainerRestartMode.None,
                MemoryBytes = 0,
                NanoCpus = 0,
                PidsLimit = 256,
                Entrypoint = ["sh", "-c", shellCommand]
            };
        }
    }
}
