namespace XE_Local_AI_Engine.Tests.ExternalApps;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Catalog;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Tests.Containers;
using XE_Local_AI_Engine.Tests.ContainerSandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The only tests that put a whole two-service application through the real <c>ExternalAppService</c> against a
///     real Docker Engine.
///     <para>
///         The fake-based suites prove the state machine, the refusals and the writes. Only this one proves that a
///         real daemon, asked for what the planner produces, gives back two containers on one network with exactly
///         one loopback-published port that a socket in this process can actually connect to, a bind mount the
///         application can write, a materialised asset it can read, and that stop, reset and uninstall leave the
///         daemon with nothing of ours on it.
///     </para>
///     <para>
///         <b>Opt-in.</b> Nothing runs here unless <c>XE_REQUIRE_DOCKER_TESTS=1</c>; without it every test skips
///         with a reason naming <c>scripts/run-docker-smoke-local.sh</c>, which is how they are meant to be run.
///         With it set, an unusable daemon is a FAILURE rather than a skip — "these tests did not run" is an
///         environment fact on a laptop and a broken gate on a machine that promised Docker.
///     </para>
/// </summary>
public sealed class ExternalAppRealDaemonTests
{
    /// <summary>Set to <c>1</c> where a daemon is promised (CI); an absent one is then a FAILURE, not a skip.</summary>
    private const string RequireDockerVariable = "XE_REQUIRE_DOCKER_TESTS";

    private const string ApplicationId = "real-daemon-app";
    private const string UiService = "web";
    private const string DependencyService = "worker";
    private const int UiContainerPort = 8080;
    private const string ServedDocument = "<html><body>xe-external-apps</body></html>";

    private const string PrivateApplicationId = "real-daemon-private-data-app";
    private const string PrivateService = "vault";
    private const string PrivateVolume = "state";
    private const string PrivateDirectoryName = "config";

    /// <summary>
    ///     An in-container uid that is neither root nor the engine's. 977 is the uid the live round's SearXNG image
    ///     used, which is how the defect was found.
    /// </summary>
    private const int PrivateUid = 977;

    /// <summary>
    ///     One probe and one image provisioning for the whole class. Every test needs the same answer, so probing
    ///     per test would multiply the round trips for a result that cannot differ; a skip decided here is rethrown
    ///     to each awaiting test unchanged.
    /// </summary>
    private static readonly Lazy<Task<ContainerRuntimeOptions>> DaemonGate = new(ResolveUsableDaemonAsync);

    /// <summary>
    ///     The whole lifecycle in one pass, because every step's evidence is the previous step's daemon state: a
    ///     stop that kept its containers is what makes the next start reuse them, and a reset that wiped the volume
    ///     is only meaningful against a volume the install actually wrote.
    /// </summary>
    [Test]
    public async Task RealDaemon_TwoServiceApplication_InstallsStartsStopsResetsAndUninstalls()
    {
        await using var box = await NewBoxAsync().ConfigureAwait(false);
        await box.AssertNothingOfOursIsOnTheDaemonAsync("before the install").ConfigureAwait(false);

        var installed = await box.InstallAsync().ConfigureAwait(false);
        var detail = await box.Service.GetAsync(installed).ConfigureAwait(false);

        // Exactly one published port, and it is the ui service's.
        AssertEx.Equal(expected: 1, detail.PublishedPorts.Count, "A two-service application with one ui port must publish exactly one.");
        var published = detail.PublishedPorts[0];
        AssertEx.Equal(UiService, published.Service);
        AssertEx.Equal(UiContainerPort, published.ContainerPort);

        // The binding is genuinely reachable from this process, which is the one thing an inspect can never say.
        AssertEx.Contains(await RealDaemonBox.FetchAsync(published.HostPort).ConfigureAwait(false), "xe-external-apps");

        // Both services exist, on one network, and the dependency is there too.
        var byService = await box.ListByServiceAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 2, byService.Count);
        AssertEx.True(byService.ContainsKey(DependencyService), "The dependency service's container is missing.");

        // Stop keeps the containers and the network: it is not a teardown.
        var stopped = await box.RunAsync(ExternalAppInstanceStatus.Stopped,
                                   (service, id, version) => service.StopAsync(id, version))
                               .ConfigureAwait(false);
        AssertEx.Equal(ExternalAppDesiredState.Stopped, stopped.DesiredState);
        AssertEx.Equal(expected: 2, (await box.ListByServiceAsync().ConfigureAwait(false)).Count, "Stop removed containers; it must only stop them.");

        // Start again: the same host port comes back, because the containers are reused rather than rebuilt.
        var started = await box.RunAsync(ExternalAppInstanceStatus.Running,
                                   (service, id, version) => service.StartAsync(id, version))
                               .ConfigureAwait(false);
        AssertEx.Equal(ExternalAppDesiredState.Running, started.DesiredState);

        var afterRestart = await box.Service.GetAsync(installed).ConfigureAwait(false);
        AssertEx.Equal(published.HostPort, afterRestart.PublishedPorts[0].HostPort, "A stop and start must not move the port the user bookmarked.");
        AssertEx.Contains(await RealDaemonBox.FetchAsync(afterRestart.PublishedPorts[0].HostPort).ConfigureAwait(false), "xe-external-apps");

        // Reset wipes the writable volume and rebuilds, restoring the desired state it found.
        var marker = Path.Combine(box.VolumePathFor(UiService, "data"), "written-by-the-test");
        await File.WriteAllTextAsync(marker, "gone after a reset").ConfigureAwait(false);

        var reset = await box.RunAsync(ExternalAppInstanceStatus.Running,
                                 (service, id, version) => service.ResetAsync(id, version))
                             .ConfigureAwait(false);
        AssertEx.Equal(ExternalAppDesiredState.Running, reset.DesiredState, "Reset restores the desired state it found, and the instance was running.");
        AssertEx.False(File.Exists(marker), "Reset must wipe the instance's writable volume.");

        // Uninstall takes the rows, the containers, the network and the directory.
        var storagePath = reset.StoragePath;
        _ = await box.Service.UninstallAsync(installed, reset.Version).ConfigureAwait(false);
        await box.SettleUninstalledAsync(installed).ConfigureAwait(false);

        AssertEx.False(Directory.Exists(storagePath), $"The instance directory '{storagePath}' survived the uninstall.");
        await box.AssertNothingOfOursIsOnTheDaemonAsync("after the uninstall").ConfigureAwait(false);
    }

    /// <summary>
    ///     The hardening contract, asserted against what the daemon says rather than against what was asked for.
    ///     "We passed the flag" is not verification, and the published-port interface is the field that decides
    ///     whether an installed application is reachable from this machine only or from the network.
    /// </summary>
    [Test]
    public async Task RealDaemon_Install_PublishesOnLoopbackOnlyAndAppliesTheHardeningContract()
    {
        await using var box = await NewBoxAsync().ConfigureAwait(false);

        var installed = await box.InstallAsync().ConfigureAwait(false);
        await using var runtime = await box.CreateRuntimeAsync().ConfigureAwait(false);

        var byService = await box.ListByServiceAsync().ConfigureAwait(false);
        var inspection = await runtime.InspectAsync(byService[UiService]).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, inspection.PublishedPorts.Count);
        AssertEx.Equal("127.0.0.1", inspection.PublishedPorts[0].HostIp,
            "A published port on any other interface makes an installed application reachable from the network.");

        AssertEx.Equal(ContainerRestartMode.UnlessStopped, inspection.RestartMode, "The restart policy is what keeps applications serving while the engine is down.");
        AssertEx.False(inspection.Privileged);
        AssertEx.Contains(inspection.CapabilitiesDropped, "ALL");
        AssertEx.Empty(inspection.CapabilitiesAdded);
        AssertEx.Equal(expected: 0L, inspection.MemoryBytes, "V1 sets no per-container memory ceiling; the manifest's figures gate install only.");
        AssertEx.Equal(expected: 0L, inspection.NanoCpus);
        AssertEx.Equal(expected: 512L, inspection.PidsLimit);
        AssertEx.Equal(expected: 0, inspection.DeviceCount, "No GPU device request may ever reach the daemon.");

        // The effective mounts, not the requested ones: the writable volume and the read-only materialised asset.
        AssertEx.Contains(inspection.Mounts, mount => mount.Destination == "/data" && !mount.ReadOnly);
        AssertEx.Contains(inspection.Mounts, mount => mount.Destination == "/srv/index.html" && mount.ReadOnly);

        var detail = await box.Service.GetAsync(installed).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, detail.PublishedPorts.Count);
    }

    /// <summary>
    ///     The defect the first live round found, and the only test that can find it again: an application whose own
    ///     non-root user creates <c>0700</c> directories under its bind mount.
    ///     <para>
    ///         Under a rootless daemon that in-container uid maps into the operator's subuid range and under a
    ///         rootful one it is that uid outright; either way it is NOT the engine's, so the engine can neither
    ///         traverse those directories nor unlink what is in them. The install-time write probe never sees it,
    ///         because it writes as the container's INITIAL user, which is root — which is exactly why a reset
    ///         reported "this application's storage could not be prepared" and an uninstall silently left the
    ///         instance directory on disk after a confirmation promising to delete it.
    ///     </para>
    ///     <para>
    ///         The fixture reproduces it with the mechanism curated images use: create the directory as root, hand it
    ///         to an unprivileged uid, and leave it <c>0700</c>. No <c>--user</c> is involved, because the engine
    ///         never passes one and a container started as that uid could not have created the directory either.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RealDaemon_WhenAnApplicationWritesAsItsOwnNonRootUser_ResetAndUninstallStillLeaveNothingOnDisk()
    {
        if (Environment.IsPrivilegedProcess)
        {
            Skip.Test("This process is privileged, so it could delete the application's 0700 directories itself and the test would assert the opposite of what it claims.");
            return;
        }

        await using var box = await NewBoxAsync(RealDaemonBox.PrivateDataManifest()).ConfigureAwait(false);
        await box.AssertNothingOfOursIsOnTheDaemonAsync("before the install").ConfigureAwait(false);

        var installed = await box.InstallAsync().ConfigureAwait(false);
        var privateDirectory = Path.Combine(box.VolumePathFor(PrivateService, PrivateVolume), PrivateDirectoryName);
        await AwaitPrivateDirectoryAsync(privateDirectory).ConfigureAwait(false);

        // The premise, asserted rather than assumed: this process cannot remove it, so an engine that only deleted
        // host-side would fail here. Without this the rest proves only that a deletable directory gets deleted.
        AssertEx.Throws<UnauthorizedAccessException>(() => Directory.Delete(privateDirectory, recursive: true),
            "The application's directory is removable by this process, so this box cannot reproduce the defect.");

        // A marker BESIDE the unreadable directory, in the part of the tree the engine does own. The helper removes
        // the contents of volumes/ wholesale, so this going away is the wipe having run; and the reset reaching
        // Running at all is the 0700 subtree having gone with it, because the host-side delete of the emptied tree
        // is what fails otherwise. Together they are the assertion that separates the two capability sets: a helper
        // that cannot unlink another uid's entries leaves 'config' behind, the emptiness re-check refuses, and the
        // reset below settles Failed instead.
        var marker = Path.Combine(box.VolumePathFor(PrivateService, PrivateVolume), "written-by-the-test");
        await File.WriteAllTextAsync(marker, "gone after a reset").ConfigureAwait(false);

        // Reset: the wipe goes through the helper, and the rebuilt container recreates the same directory.
        var reset = await box.RunAsync(ExternalAppInstanceStatus.Running,
                                 (service, id, version) => service.ResetAsync(id, version))
                             .ConfigureAwait(false);

        AssertEx.False(File.Exists(marker), "The reset did not wipe the volumes directory the 0700 subtree lives in.");
        await AwaitPrivateDirectoryAsync(privateDirectory).ConfigureAwait(false);

        // Uninstall: the rows, the containers, the network AND the whole instance directory, 0700 subtree included.
        _ = await box.Service.UninstallAsync(installed, reset.Version).ConfigureAwait(false);
        await box.SettleUninstalledAsync(installed).ConfigureAwait(false);

        AssertEx.False(Directory.Exists(reset.StoragePath),
            $"The instance directory '{reset.StoragePath}' survived the uninstall, which is the defect the helper exists to fix.");
        await box.AssertNothingOfOursIsOnTheDaemonAsync("after the uninstall").ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits for the container to have created its private directory. A gate on the product's own effect, never a
    ///     sleep: the daemon reports the container running the instant it starts, and the shell inside it needs a
    ///     moment to reach the <c>chown</c>.
    /// </summary>
    private static async Task AwaitPrivateDirectoryAsync(string privateDirectory)
    {
        await AssertEx.EventuallyAsync(() => Directory.Exists(privateDirectory),
            TimeSpan.FromSeconds(60),
            $"The fixture application never created '{privateDirectory}', so there is no unreadable directory to test against.").ConfigureAwait(false);
    }

    private static async Task<RealDaemonBox> NewBoxAsync(ApplicationManifest? manifest = null)
    {
        return await RealDaemonBox.CreateAsync(await DaemonGate.Value.ConfigureAwait(false), manifest ?? RealDaemonBox.Manifest())
                                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     The endpoints to try, each named by the PRODUCTION resolver. The second exists because that resolver
    ///     deliberately stops at an existing <c>/var/run/docker.sock</c> and never falls through to a per-user
    ///     socket — correct for a product whose operator attests to one daemon, and the reason every test here would
    ///     otherwise skip on a box with a root-owned system socket and a rootless daemon of its own.
    /// </summary>
    private static IEnumerable<ContainerRuntimeOptions> DaemonCandidates()
    {
        var options = new ContainerRuntimeOptions();
        yield return options;

        if (OperatingSystem.IsWindows())
        {
            yield break;
        }

        if (DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint).Source != DockerDaemonEndpointSource.DefaultUnixSocket)
        {
            yield break;
        }

        var runtimeDirectory = Environment.GetEnvironmentVariable(DockerDaemonEndpointResolver.UserRuntimeDirectoryVariable);
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            yield break;
        }

        var userSocket = Path.Combine(runtimeDirectory, "docker.sock");
        if (!File.Exists(userSocket))
        {
            yield break;
        }

        yield return options with
        {
            DaemonEndpoint = "unix://" + userSocket
        };
    }

    private static async Task<ContainerRuntimeOptions> ResolveUsableDaemonAsync()
    {
        RequireOptIn();

        var attempts = new List<string>();

        foreach (var candidate in DaemonCandidates())
        {
            var endpoint = DockerDaemonEndpointResolver.Resolve(candidate.DaemonEndpoint);
            DockerDaemonIdentity identity;

            await using (var client = RealDaemonBox.BuildRuntime(candidate))
            {
                try
                {
                    identity = await client.ProbeAsync().ConfigureAwait(false);
                }
                catch (DockerRuntimeException exception)
                {
                    attempts.Add($"{exception.Status} at '{endpoint.Display}' (found via {endpoint.Source}): {exception.Message}");
                    continue;
                }
            }

            if (!identity.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase))
            {
                throw Unavailable($"the reachable Docker daemon at '{endpoint.Display}' runs '{identity.OperatingSystem}' containers, not Linux. "
                                  + "Bind storage and loopback port publishing mean something different there and cannot be verified.");
            }

            await EnsureImageAsync(candidate).ConfigureAwait(false);
            return candidate;
        }

        throw Unavailable("no usable Docker daemon. Tried " + string.Join(" | ", attempts)
                                                            + " Start Docker, or point DOCKER_HOST at a daemon this user can open, and re-run.");
    }

    /// <summary>
    ///     Makes the one pinned image present, through the PRODUCTION pull. CI pre-pulls it as its own step so a
    ///     registry blip fails there as infrastructure; this is what keeps a fresh laptop from skipping the class.
    /// </summary>
    private static async Task EnsureImageAsync(ContainerRuntimeOptions options)
    {
        await using var runtime = RealDaemonBox.BuildRuntime(options);

        if (await runtime.ImageExistsAsync(ContainerRuntimeTestImages.Busybox).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await runtime.PullImageAsync(ContainerRuntimeTestImages.Busybox, progress: null).ConfigureAwait(false);
        }
        catch (DockerRuntimeException exception)
        {
            throw Unavailable($"the pinned test image '{ContainerRuntimeTestImages.Busybox}' is not on this daemon and pulling it failed "
                              + $"({exception.Message}). Run `docker pull {ContainerRuntimeTestImages.Busybox}` and re-run.");
        }
    }

    /// <summary>
    ///     The one switch. Set <c>XE_REQUIRE_DOCKER_TESTS=1</c> and these tests run, failing rather than skipping
    ///     when no daemon is usable; leave it unset and they skip with a reason naming the runner.
    ///     <para>
    ///         They used to run whenever a socket happened to be present, and CI forced them on. That made Docker
    ///         Hub reachability a hard dependency of every pull request, for suites whose wire-shape half is now
    ///         covered without a daemon by the fake server. What is left here is what only a real daemon can settle,
    ///         and that belongs to an opt-in pre-RC smoke rather than to the PR gate.
    ///     </para>
    /// </summary>
    private static void RequireOptIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RequireDockerVariable), "1", StringComparison.Ordinal))
        {
            throw new SkipTestException($"SKIPPED — opt-in: set {RequireDockerVariable}=1 (scripts/run-docker-smoke-local.sh) to run "
                                        + "the real-daemon tests for the External Apps install path. CI covers the wire shape without a daemon through "
                                        + "XE-Local-AI-Engine.Testing.FakeDocker; these prove what only a real daemon can.");
        }
    }

    /// <summary>
    ///     Always a failure, never a skip: <see cref="RequireOptIn" /> has already turned away a run that did not
    ///     ask for a daemon, so reaching here means one was PROMISED and is not usable.
    /// </summary>
    private static Exception Unavailable(string reason)
    {
        var message = reason + " These are the ONLY tests that put a whole application through the real service against a real daemon; "
                             + "a green run without them is not evidence that an installed application would start.";

        return new InvalidOperationException($"REQUIRED — {RequireDockerVariable}=1, so this is a failure rather than a skip: {message}");
    }

    /// <summary>
    ///     One test's node: a temp data directory, a real SQLite instance store, the real resolver over the real
    ///     runtime factory, and the real service. Everything is labelled with an install id derived from that temp
    ///     directory, so nothing this class creates can be confused with — or clean up — anything else on a shared
    ///     developer daemon.
    /// </summary>
    private sealed class RealDaemonBox : IAsyncDisposable
    {
        private readonly ExternalAppStorageLayout _layout;

        private readonly ApplicationManifest _manifest;

        // Held as a bare IDisposable and never as the typed lifetime: a CancellationToken source in scope makes
        // every call in this fixture look to S8949 like one that forgot to thread a token.
        private readonly IDisposable _lifetimeHandle;
        private readonly ContainerRuntimeOptions _options;
        private readonly ServiceProvider _provider;
        private readonly IContainerRuntimeResolver _resolver;

        private RealDaemonBox(ServiceProvider provider,
            IContainerRuntimeResolver resolver,
            ExternalAppService service,
            ExternalAppStorageLayout layout,
            IDisposable lifetimeHandle,
            ContainerRuntimeOptions options,
            string root,
            string installId,
            ApplicationManifest manifest)
        {
            _manifest = manifest;
            _provider = provider;
            _resolver = resolver;
            _layout = layout;
            _lifetimeHandle = lifetimeHandle;
            _options = options;
            Service = service;
            Root = root;
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ExternalAppLabels.Owner] = ExternalAppLabels.OwnerValue,
                [ExternalAppLabels.Install] = installId
            };
        }

        public ExternalAppService Service { get; }

        public string Root { get; }

        /// <summary>The owner plus install filter every listing and every cleanup in this class uses.</summary>
        public IReadOnlyDictionary<string, string> Labels { get; }

        public Guid InstanceId { get; private set; }

        /// <summary>A runtime client built exactly the way the composition root builds one.</summary>
        public static IContainerRuntime BuildRuntime(ContainerRuntimeOptions options)
        {
            return new DockerContainerRuntimeFactory(new StaticOptionsMonitor<ContainerRuntimeOptions>(options), NullLoggerFactory.Instance)
                .CreateRuntime(DockerDaemonEndpointResolver.Resolve(options.DaemonEndpoint));
        }

        public static async Task<RealDaemonBox> CreateAsync(ContainerRuntimeOptions runtimeOptions, ApplicationManifest manifest)
        {
            var root = Path.Combine(Path.GetTempPath(), "xe-ext-apps-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var dataDirectory = new FakeNodeDataDirectory(root);
            var appOptions = Options.Create(new ExternalAppsOptions
            {
                Enabled = true,
                InstanceRoot = root,

                // Real containers on a real daemon: a real clock and a budget wide enough for a cold start on a
                // contended box, and narrow enough that a hung service fails the run rather than hanging it.
                ServiceReadyTimeoutSeconds = 120,
                StopGracePeriodSeconds = 5
            });

            var services = new ServiceCollection();
            services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
            services.AddDbContext<NodeChatDbContext>(builder => builder.UseSqlite($"Data Source={Path.Combine(root, "node.sqlite")}"));
            services.AddScoped<IExternalAppInstanceStore, ExternalAppInstanceStore>();
            services.AddSingleton(Catalog(manifest));

            // The REAL resolver over the REAL factory, with an in-memory pin: the production attestation store is a
            // single unkeyed file under the node data directory, and a test using it would pin the developer's own
            // daemon and race the Development Mode suite, which pins the same file.
            var settingsStore = Substitute.For<INodeSettingsStore>();
            settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new StoredNodeSettings()));
            var resolver = new ContainerRuntimeResolver(settingsStore,
                new StaticOptionsMonitor<ContainerRuntimeOptions>(runtimeOptions),
                new DockerContainerRuntimeFactory(new StaticOptionsMonitor<ContainerRuntimeOptions>(runtimeOptions), NullLoggerFactory.Instance),
                new InMemoryDaemonAttestationStore(),
                TimeProvider.System,
                NullLogger<ContainerRuntimeResolver>.Instance);
            services.AddSingleton<IContainerRuntimeResolver>(resolver);

            var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.EnsureCreatedAsync().ConfigureAwait(false);
            }

            var layout = new ExternalAppStorageLayout(dataDirectory, appOptions);
            var lifetime = new TestHostLifetime();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var service = new ExternalAppService(scopeFactory,
                layout,
                new ExternalAppResourceGate(Audit(), dataDirectory, new DriveInfoFreeSpaceProbe()),
                new ExternalAppInstanceGate(),
                new ExternalAppOperationRunner(scopeFactory, lifetime, NullLogger<ExternalAppOperationRunner>.Instance),
                new NoOpExternalAppEventPublisher(),
                dataDirectory,
                appOptions,
                // No bridge in the real-daemon box: it exercises the container runtime, not the bridge, and a
                // container told about an endpoint nothing is listening on would be told a falsehood.
                new ContainerBridgeEndpointSource(endpoint: null),
                TimeProvider.System,
                NullLogger<ExternalAppService>.Instance);

            return new RealDaemonBox(provider,
                resolver,
                service,
                layout,
                lifetime,
                runtimeOptions,
                root,
                DockerSandboxRuntimeProvider.BuildInstallId(root),
                manifest);
        }

        public Task<IContainerRuntime> CreateRuntimeAsync()
        {
            return _resolver.CreateRuntimeAsync();
        }

        /// <summary>
        ///     Installs the fixture application, retrying ONCE and only when the attempt lost a host port. Every
        ///     other failure propagates: a retry that swallowed them would turn a real defect into a slow pass.
        /// </summary>
        public async Task<Guid> InstallAsync()
        {
            var retried = false;
            while (true)
            {
                var admitted = await Service.InstallAsync(new InstallCommand(_manifest.Id,
                                                DisplayName: null,
                                                _manifest.ManifestVersion,
                                                _manifest.ManifestSha256,
                                                new Dictionary<string, string>(StringComparer.Ordinal),
                                                AcceptPermissions: true))
                                            .ConfigureAwait(false);

                InstanceId = admitted.Id;
                var row = await SettleAsync(admitted.Id, ExternalAppInstanceStatus.Running, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);
                if (row.Status == ExternalAppInstanceStatus.Running)
                {
                    return admitted.Id;
                }

                if (retried || row.FailureCategory != ExternalAppFailureCategory.PortUnavailable)
                {
                    throw new AssertionException($"The install settled {row.Status} ({row.FailureCategory}): {row.FailureSummary}");
                }

                retried = true;

                // Another process took the port between the probe and the create. Clean the attempt away and try
                // once more; the pipeline already replanned once inside itself.
                _ = await Service.UninstallAsync(admitted.Id, row.Version).ConfigureAwait(false);
                await SettleUninstalledAsync(admitted.Id).ConfigureAwait(false);
            }
        }

        /// <summary>Issues one lifecycle command against the row's current version and waits for it to settle.</summary>
        public async Task<ExternalAppInstanceSnapshot> RunAsync(ExternalAppInstanceStatus expected,
            Func<ExternalAppService, Guid, long, Task<ExternalAppInstanceSummary>> command)
        {
            var row = await ReadAsync(InstanceId).ConfigureAwait(false)
                      ?? throw new AssertionException($"Instance {InstanceId:N} is gone before the command could be issued.");

            _ = await command(Service, InstanceId, row.Version).ConfigureAwait(false);
            var settled = await SettleAsync(InstanceId, expected, ExternalAppInstanceStatus.Failed).ConfigureAwait(false);

            return settled.Status == expected
                ? settled
                : throw new AssertionException($"The command settled {settled.Status} ({settled.FailureCategory}): {settled.FailureSummary}");
        }

        public async Task<ExternalAppInstanceSnapshot?> ReadAsync(Guid instanceId)
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IExternalAppInstanceStore>().GetAsync(instanceId).ConfigureAwait(false);
        }

        public async Task<ExternalAppInstanceSnapshot> SettleAsync(Guid instanceId, params ExternalAppInstanceStatus[] expected)
        {
            ExternalAppInstanceSnapshot? row = null;
            await AssertEx.EventuallyAsync(() =>
                {
                    row = ReadAsync(instanceId).GetAwaiter().GetResult();
                    return row is not null && Array.IndexOf(expected, row.Status) >= 0;
                },
                TimeSpan.FromMinutes(3),
                $"Instance {instanceId:N} never reached {string.Join(" or ", expected)}.").ConfigureAwait(false);

            return row!;
        }

        public async Task SettleUninstalledAsync(Guid instanceId)
        {
            await AssertEx.EventuallyAsync(() => ReadAsync(instanceId).GetAwaiter().GetResult() is null,
                TimeSpan.FromMinutes(3),
                $"Instance {instanceId:N} was never removed.").ConfigureAwait(false);
        }

        /// <summary>This installation's containers, keyed by the service label each one carries.</summary>
        public async Task<Dictionary<string, string>> ListByServiceAsync()
        {
            await using var runtime = await CreateRuntimeAsync().ConfigureAwait(false);
            var byService = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var container in await runtime.ListContainersDetailedAsync(Labels).ConfigureAwait(false))
            {
                if (container.Labels.TryGetValue(ExternalAppLabels.Service, out var serviceName))
                {
                    byService[serviceName] = container.Id;
                }
            }

            return byService;
        }

        public string VolumePathFor(string serviceName, string volumeName)
        {
            return Path.Combine(_layout.Describe(InstanceId).VolumesRoot, serviceName, volumeName);
        }

        /// <summary>Reads the served document over the published loopback binding — the proof an inspect cannot give.</summary>
        public static async Task<string> FetchAsync(int hostPort)
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            var address = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{hostPort}/index.html"));

            // The daemon publishes the binding the instant the container starts, but busybox's httpd needs a moment
            // to accept on it. A gate on the product's own signal, never a sleep.
            HttpResponseMessage response = null!;
            await AssertEx.EventuallyAsync(() =>
                {
                    try
                    {
                        response = client.GetAsync(address).GetAwaiter().GetResult();
                        return response.IsSuccessStatusCode;
                    }
                    catch (HttpRequestException)
                    {
                        return false;
                    }
                },
                TimeSpan.FromSeconds(30),
                $"Nothing answered on the published binding 127.0.0.1:{hostPort}.").ConfigureAwait(false);

            using var answered = response;
            return await answered.Content.ReadAsStringAsync().ConfigureAwait(false);
        }

        /// <summary>The label-scoped emptiness check the operator would run by hand: nothing of ours on the daemon.</summary>
        public async Task AssertNothingOfOursIsOnTheDaemonAsync(string when)
        {
            await using var runtime = await CreateRuntimeAsync().ConfigureAwait(false);

            AssertEx.Empty(await runtime.ListContainersDetailedAsync(Labels).ConfigureAwait(false),
                $"Containers carrying this installation's labels exist {when}.");
            AssertEx.Empty(await runtime.ListNetworksAsync(Labels).ConfigureAwait(false),
                $"Networks carrying this installation's labels exist {when}.");
        }

        public async ValueTask DisposeAsync()
        {
            // Best effort and BY LABEL: a test that already failed must not have its verdict replaced by a cleanup
            // error, and a name match would remove another installation's containers.
            try
            {
                await using var runtime = BuildRuntime(_options);

                foreach (var container in await runtime.ListContainersDetailedAsync(Labels).ConfigureAwait(false))
                {
                    await runtime.RemoveContainerAsync(container.Id).ConfigureAwait(false);
                }

                foreach (var network in await runtime.ListNetworksAsync(Labels).ConfigureAwait(false))
                {
                    await runtime.RemoveNetworkAsync(network).ConfigureAwait(false);
                }
            }
            catch (DockerRuntimeException)
            {
                // Already gone, or the daemon went away with the test. Cleanup must not replace a verdict.
            }

            _lifetimeHandle.Dispose();
            await _provider.DisposeAsync().ConfigureAwait(false);

            if (!Directory.Exists(Root))
            {
                return;
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                // A rootful daemon can leave root-owned files in an engine-created bind mount. A temp directory left
                // behind is a smaller problem than failing the class over it.
            }
            catch (IOException)
            {
                // As above.
            }
        }

        private static IApplicationCatalogProvider Catalog(ApplicationManifest manifest)
        {
            var catalog = Substitute.For<IApplicationCatalogProvider>();
            var document = new ExternalAppCatalogDocument(SchemaVersion: 1, "2026-09-11T00:00:00Z", [manifest]);

            catalog.GetCatalogAsync(Arg.Any<CancellationToken>())
                   .Returns(Task.FromResult(new ExternalAppCatalogSnapshot(document,
                       ExternalAppCatalogSource.Bundled,
                       FetchedAtUtc: null,
                       SourceUrl: null,
                       LastRefreshFailure: null)));
            catalog.GetApplicationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                   .Returns(call => Task.FromResult(string.Equals(call.ArgAt<string>(0), manifest.Id, StringComparison.Ordinal) ? manifest : null));

            return catalog;
        }

        private static IRuntimeDeviceAudit Audit()
        {
            var audit = Substitute.For<IRuntimeDeviceAudit>();
            audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new HardwareProfile
                 {
                     TotalRamBytes = 32L * 1024 * 1024 * 1024,
                     AvailableRamBytes = 32L * 1024 * 1024 * 1024,
                     VramBytes = 0,
                     AvailableVramBytes = null,
                     VramKnown = false,
                     GpuVendor = GpuVendor.None,
                     GpuAccelAvailable = false,
                     CpuCores = 8,
                     FreeDiskBytes = 512L * 1024 * 1024 * 1024
                 }));

            return audit;
        }

        /// <summary>
        ///     Two busybox containers: one serving the materialised asset over the published loopback port, one sleeping
        ///     so the dependency edge is real. Both roles run the same pinned digest, so the pull is seconds and CI's
        ///     pre-pull step already covers it.
        /// </summary>
        /// <summary>
        ///     One busybox service that creates a <c>0700</c> directory under its bind mount and hands it to an
        ///     unprivileged in-container uid, then keeps running. The <c>chown</c> is what makes the directory
        ///     unreachable from the engine, and it runs as root because that is how a curated image does it: create,
        ///     hand over, drop. No port, no asset — the storage is the whole subject.
        /// </summary>
        public static ApplicationManifest PrivateDataManifest()
        {
            var vault = new ApplicationService(PrivateService,
                ContainerRuntimeTestImages.Busybox,
                "1.37",
                Entrypoint: null,
                Command:
                [
                    "sh",
                    "-c",
                    string.Create(CultureInfo.InvariantCulture,
                        $"mkdir -m700 /data/{PrivateDirectoryName} && touch /data/{PrivateDirectoryName}/secret "
                        + $"&& chown -R {PrivateUid}:{PrivateUid} /data/{PrivateDirectoryName} && sleep 3600")
                ],
                new Dictionary<string, string>(StringComparer.Ordinal),
                Ports: [],
                [new ApplicationStorage(PrivateVolume, "/data")],
                Files: [],
                Healthcheck: null,
                DependsOn: [],

                // Every application container drops ALL capabilities, so the chown below needs this one back. It is
                // in Docker's own default set and therefore in the catalog's allow-list — and it is what a curated
                // image that hands its data directory to its own user needs too, which is the point.
                CapAdd: ["CHOWN"],
                ExtraHosts: [],
                ReadOnlyRootFilesystem: false);

            return new ApplicationManifest(PrivateApplicationId,
                ManifestVersion: 1,
                "0000000000000000000000000000000000000000000000000000000000000000",
                "Private Data App",
                "One busybox service that writes as its own user.",
                "A fixture whose storage the engine's own uid cannot remove.",
                "https://example.invalid",
                "MIT",
                "firstParty",
                "1.37",
                ["containers", "networks", "bindStorage"],
                new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none"),
                new ApplicationResources(MinimumMemoryMb: 64, RecommendedMemoryMb: 128, CpuHint: 1, PidsLimit: 512),
                [vault],
                Variables: []);
        }

        public static ApplicationManifest Manifest()
        {
            var asset = Encoding.UTF8.GetBytes(ServedDocument);

            var web = new ApplicationService(UiService,
                ContainerRuntimeTestImages.Busybox,
                "1.37",
                Entrypoint: null,
                Command: ["httpd", "-f", "-p", UiContainerPort.ToString(CultureInfo.InvariantCulture), "-h", "/srv"],
                new Dictionary<string, string>(StringComparer.Ordinal),
                [new ApplicationPort(UiContainerPort, "ui", PreferredHostPort: null, "/index.html")],
                [new ApplicationStorage("data", "/data")],
                [new ApplicationFile("index.html", "/srv/index.html", Convert.ToHexStringLower(SHA256.HashData(asset)), Convert.ToBase64String(asset))],
                Healthcheck: null,
                [new ApplicationDependency(DependencyService, "started")],
                CapAdd: [],
                ExtraHosts: [],
                ReadOnlyRootFilesystem: false);

            var worker = new ApplicationService(DependencyService,
                ContainerRuntimeTestImages.Busybox,
                "1.37",
                Entrypoint: null,
                Command: ["sleep", "3600"],
                new Dictionary<string, string>(StringComparer.Ordinal),
                Ports: [],
                Storage: [],
                Files: [],
                Healthcheck: null,
                DependsOn: [],
                CapAdd: [],
                ExtraHosts: [],
                ReadOnlyRootFilesystem: false);

            return new ApplicationManifest(ApplicationId,
                ManifestVersion: 1,
                "0000000000000000000000000000000000000000000000000000000000000000",
                "Real Daemon App",
                "Two busybox services.",
                "A two-service fixture used by the External Apps real-daemon coverage.",
                "https://example.invalid",
                "MIT",
                "firstParty",
                "1.37",
                ["containers", "networks", "bindStorage"],
                new ApplicationPermissions(Internet: true, LocalNetwork: false, "none", "none"),
                new ApplicationResources(MinimumMemoryMb: 64, RecommendedMemoryMb: 128, CpuHint: 1, PidsLimit: 512),
                [web, worker],
                Variables: []);
        }
    }
}
