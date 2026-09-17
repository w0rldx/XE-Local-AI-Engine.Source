namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Testing.FakeDocker;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Development Mode sandbox provider against a fake Docker Engine, with no daemon anywhere.
///     <para>
///         What belongs here is what a scripted daemon can answer truthfully: that the provider reaches a daemon and
///         pins it, that it asks for the network mode its policy says it should, that a kill really removes the
///         container, and that a copy whose destination escapes the workspace is refused on this side of the mount.
///         What does NOT belong here is anything whose subject is the daemon or the kernel — whether a flag was
///         honoured, whether egress is really blocked, whether a uid maps. Those read back through a fake that echoes
///         the request, which would make the assertion circular, and they stay in
///         <see cref="DockerSandboxRealDaemonTests" />.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DockerSandboxFakeServerTests
{
    /// <summary>The image the fake daemon is told it holds. Digest-pinned because the provider refuses anything else.</summary>
    private const string TestImage = "alpine@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce";

    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 7, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Replaces <c>RealDaemon_Preflight_ReachesTheDaemonAndPinsIt</c>.</summary>
    [Test]
    public async Task Preflight_ReachesTheDaemonAndPinsIt()
    {
        await using var docker = await FakeDockerServer.StartAsync(new FakeDockerOptions
        {
            DaemonId = "fakedaemon:preflight"
        });

        using var attestationRoot = new TemporaryDirectory();
        var options = Options(docker);
        var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(options);

        // The real attestation store, but rooted in a directory this test owns. The production one is a single
        // unkeyed file under the node data directory, and a test that wrote to it would pin the developer's own node
        // to a daemon that exists only while this test runs.
        using var store = new DockerDaemonAttestationStore(new FixedNodeDataDirectory(attestationRoot.Path),
            NullLogger<DockerDaemonAttestationStore>.Instance);
        var service = new DockerDaemonPreflightService(monitor,
            new DockerDotNetRuntimeClientFactory(monitor, TimeProvider.System),
            store,
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerDaemonPreflightService>.Instance);

        var preflight = await service.InspectAsync();

        AssertEx.Equal(DockerDaemonPreflightStatus.Ready, preflight.Status);
        var observed = AssertEx.NotNull(preflight.ObservedDaemon);
        AssertEx.Equal("fakedaemon:preflight", observed.DaemonId);
        AssertEx.NotNullOrEmpty(observed.ServerVersion);

        // Pinned to the daemon it observed, which is the whole point: the pin is what makes a substituted daemon
        // visible on the next preflight rather than silently accepted.
        AssertEx.Equal(AssertEx.NotNull(preflight.PinnedDaemon).DaemonId, observed.DaemonId);
    }

    /// <summary>
    ///     Replaces <c>RealDaemon_WhenUnrestrictedIsRequested_TheContainerIsCreatedOnTheDefaultBridge</c>. Egress
    ///     itself was never asserted there either; the applied network mode is the guarantee the provider makes, and
    ///     the provider is what chooses it.
    /// </summary>
    [Test]
    [Arguments(SandboxNetworkPolicy.None, "none")]
    [Arguments(SandboxNetworkPolicy.Unrestricted, "bridge")]
    public async Task TheRequestedNetworkPolicy_BecomesTheContainersNetworkMode(SandboxNetworkPolicy policy, string expectedMode)
    {
        await using var fixture = await SandboxFixture.CreateAsync(policy);

        var settings = await fixture.Client.InspectContainerAsync(fixture.ContainerId);

        AssertEx.Equal(expectedMode, settings.NetworkMode);
        AssertEx.NotEqual("host", settings.NetworkMode);

        // Everything else the hardening contract requires still holds under BOTH postures; only egress moved.
        // It reads the echoed HostConfig, so what it proves is that the provider still SENDS the full set — the
        // half that would otherwise have been lost when this case left the real-daemon suite. Whether the daemon
        // honours it stays with RealDaemon_CreatedContainer_ReadsBackEveryHardeningGuarantee.
        AssertEx.Empty(DockerSandboxHardening.FindViolations(fixture.Specification, settings, daemonIsRootless: false));
    }

    /// <summary>
    ///     The INPUT to the capability-gated decision in
    ///     <c>RealDaemon_DevelopmentShapedRun_ReadsBackNoEgressFromTheCapabilityGatedDecision</c>, which keeps the
    ///     half that probes egress from inside the container.
    ///     <para>
    ///         Only the input. <c>DevelopmentWorkspaceProvider.ResolveAgentFacingNetworkPolicy</c> is private, so
    ///         this cannot call it, and a test that copied its ternary into itself would assert its own copy — it
    ///         would stay green if the production branch inverted tomorrow, which is the one regression such a
    ///         test claims to catch. So the honest claim is the flag the resolver reads: the container backend
    ///         advertises network policy, which is what makes Development Mode ask for denial rather than fall
    ///         back to unrestricted. No daemon and no sandbox are needed to read it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheContainerBackend_AdvertisesNetworkPolicySupport()
    {
        using var workspace = new TemporaryDirectory();
        var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(new ContainerSandboxOptions());
        await using var provider = new DockerSandboxRuntimeProvider(monitor,
            new DockerDotNetRuntimeClientFactory(monitor, TimeProvider.System),
            new FixedNodeDataDirectory(workspace.Path),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        AssertEx.True(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsNetworkPolicy),
            "the container backend no longer advertises network policy, so Development Mode would fall back to "
            + "Unrestricted for its agent-facing sandbox instead of asking for denial.");
    }

    /// <summary>Replaces <c>RealDaemon_KillAsync_RemovesTheContainerFromTheDaemon</c>.</summary>
    [Test]
    public async Task KillAsync_RemovesTheContainerFromTheDaemon()
    {
        await using var fixture = await SandboxFixture.CreateAsync(SandboxNetworkPolicy.None);
        var containerId = fixture.ContainerId;

        await fixture.Provider.KillAsync(fixture.Handle);

        // Gone from the daemon, not merely forgotten by the provider: a handle the provider dropped while the
        // container kept running is exactly the leak the startup sweep exists to clean up after.
        await AssertEx.ThrowsAsync<DockerRuntimeException>(() => fixture.Client.InspectContainerAsync(containerId));
        AssertEx.Empty(fixture.Docker.State.Containers.Keys);
    }

    /// <summary>
    ///     Replaces <c>RealDaemon_CopyInto_RefusesADestinationThatEscapesTheWorkspace</c>.
    ///     <para>
    ///         It never needed a real daemon: <c>DockerSandboxRuntimeProvider.CopyIntoAsync</c> maps the destination
    ///         to the mount's HOST path and hands it to <c>DockerWorkspaceHostFiles.WriteAsync</c>, which applies the
    ///         containment guard before any <c>IDockerRuntimeClient</c> member is called. A daemon is needed only to
    ///         own a live handle for the copy to be made against.
    ///     </para>
    /// </summary>
    [Test]
    public async Task CopyInto_RefusesADestinationThatEscapesTheWorkspace()
    {
        await using var fixture = await SandboxFixture.CreateAsync(SandboxNetworkPolicy.None);

        var source = Path.Combine(fixture.WorkspaceRoot, "..", "escape-source.txt");
        await File.WriteAllTextAsync(source, "should-not-land");

        await AssertEx.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Provider.CopyIntoAsync(fixture.Handle,
            new SandboxCopyRequest
            {
                SourcePath = source,
                DestinationPath = "/../escaped.txt"
            }));

        AssertEx.False(File.Exists(Path.Combine(fixture.WorkspaceRoot, "..", "escaped.txt")),
            "The refused copy landed on disk anyway, so the containment guard is not what stopped it.");
    }

    private static ContainerSandboxOptions Options(FakeDockerServer docker)
    {
        return new ContainerSandboxOptions
        {
            Image = TestImage,
            DaemonEndpoint = docker.BaseAddress.ToString(),
            // Pinned, unlike the real-daemon suite's options, which leave these unset because which in-container
            // id can use a bind mount is a property of the daemon. A fake daemon maps no ids at all, so pinning
            // them costs nothing and lets the fixture rebuild the exact specification the provider applied.
            UserId = 1000,
            GroupId = 1000,
            ScratchSizeMb = 64,
            MemoryMb = 512,
            CpuCount = 2,
            PidsLimit = 256,
            DaemonProbeTimeoutSeconds = 10
        };
    }

    /// <summary>
    ///     The real <see cref="DockerSandboxRuntimeProvider" />, one sandbox it created, and the fake daemon behind
    ///     it — the shape <see cref="DockerSandboxRealDaemonTests" />'s own fixture has, with the endpoint pointed at
    ///     a server instead of a socket and nothing else changed.
    /// </summary>
    private sealed class SandboxFixture : IAsyncDisposable
    {
        private readonly TemporaryDirectory _workspace;

        private SandboxFixture(FakeDockerServer docker,
            DockerSandboxRuntimeProvider provider,
            SandboxHandle handle,
            IDockerRuntimeClient client,
            string containerId,
            DockerContainerSpecification specification,
            TemporaryDirectory workspace)
        {
            Docker = docker;
            Provider = provider;
            Handle = handle;
            Client = client;
            ContainerId = containerId;
            Specification = specification;
            _workspace = workspace;
        }

        public FakeDockerServer Docker { get; }

        public DockerSandboxRuntimeProvider Provider { get; }

        public SandboxHandle Handle { get; }

        public IDockerRuntimeClient Client { get; }

        public string ContainerId { get; }

        /// <summary>
        ///     The specification the provider applied, rebuilt from what the handle REPORTS rather than from the
        ///     request, so a hardening check runs against what was actually sent.
        /// </summary>
        public DockerContainerSpecification Specification { get; }

        public string WorkspaceRoot => Path.Combine(_workspace.Path, "workspace");

        public static async Task<SandboxFixture> CreateAsync(SandboxNetworkPolicy networkPolicy)
        {
            var docker = await FakeDockerServer.StartAsync();
            var workspace = new TemporaryDirectory();

            try
            {
                docker.State.SeedImage(TestImage);

                // The provider ends its create by touching a probe file inside the container and looking for it on
                // the host: that round trip is how it detects a uid mapping the daemon's own read-back cannot
                // report. Without this the provider correctly refuses every sandbox the fake creates.
                docker.State.WritesThroughBindMounts = true;
                Directory.CreateDirectory(Path.Combine(workspace.Path, "workspace"));

                var options = Options(docker);
                var monitor = new StaticOptionsMonitor<ContainerSandboxOptions>(options);
                var factory = new DockerDotNetRuntimeClientFactory(monitor, TimeProvider.System);
                var provider = new DockerSandboxRuntimeProvider(monitor,
                    factory,
                    new FixedNodeDataDirectory(workspace.Path),
                    new FixedTimeProvider(FixedNow),
                    NullLogger<DockerSandboxRuntimeProvider>.Instance);

                var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
                {
                    AttachKey = new SandboxAttachKey
                    {
                        OwnerUserId = Guid.NewGuid().ToString("N"),
                        NodeId = "fake-server-node",
                        ProviderName = DockerSandboxRuntimeProvider.Name,
                        RuntimeProfile = "development",
                        ManifestVersion = 1
                    },
                    RuntimeProfile = "development",
                    NetworkPolicy = networkPolicy,
                    TrustedHostWorkspace = new SandboxTrustedHostWorkspace
                    {
                        RootPath = Path.Combine(workspace.Path, "workspace")
                    },
                    Mounts = []
                });

                var client = factory.Create(DockerDaemonEndpointResolver.Resolve(options));
                var containerId = docker.State.Containers.Keys.Single();

                // The id readers are never called: the options pin UserId and GroupId, so ResolveIdentity takes
                // them verbatim and the rebuild matches what the provider itself resolved.
                var specification = DockerSandboxHardening.BuildSpecification(options,
                    DockerSandboxRuntimeProvider.ResolveIdentity(options, daemonIsRootless: false, () => 1000, () => 1000),
                    "xe-dev-" + handle.SandboxId,
                    handle.SandboxId,
                    DockerSandboxRuntimeProvider.BuildInstallId(workspace.Path),
                    [
                        .. handle.Mounts.Select(mount => new DockerBindMount
                        {
                            HostPath = mount.HostPath,
                            ContainerPath = mount.SandboxPath,
                            ReadOnly = mount.ReadOnly,
                            Propagation = "private"
                        })
                    ],
                    requestedLimits: null,
                    networkPolicy);

                return new SandboxFixture(docker, provider, handle, client, containerId, specification, workspace);
            }
            catch
            {
                workspace.Dispose();
                await docker.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // The provider FIRST, and before the workspace or the server go: every sandbox it created holds its
            // own Docker client, and this fixture's inspection client is not one of them. Disposing only the
            // inspection client left those behind, holding sockets against a server about to stop.
            await Provider.DisposeAsync();
            await Client.DisposeAsync();
            _workspace.Dispose();
            await Docker.DisposeAsync();
        }
    }
}
