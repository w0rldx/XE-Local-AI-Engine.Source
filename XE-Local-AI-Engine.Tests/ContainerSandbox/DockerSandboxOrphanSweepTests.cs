namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The startup sweep that collects Development Mode containers a previous run leaked.
///     <para>
///         The provider tracks its containers in an in-memory dictionary that dies with the process, so a hard host
///         kill between create and teardown leaves a container nothing references — and, because a container owns its
///         <c>xe-dev-&lt;sandboxId&gt;</c> name until removed, the same attach key then collides with it on every
///         subsequent start. <c>SandboxOrphanReaper</c> does not cover this: it reads on-disk markers written by the
///         process provider and never speaks to a daemon.
///     </para>
///     <para>
///         What is actually being defended here is the <em>narrowness</em> of the removal. Removing a container is
///         irreversible and this sweep runs unattended at startup, so the cases below are weighted towards what must
///         survive it: another installation's container, and this process's own.
///     </para>
/// </summary>
public sealed class DockerSandboxOrphanSweepTests
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 8, day: 25, hour: 9, minute: 0, second: 0, TimeSpan.Zero);

    [Test]
    public async Task Sweep_RemovesAContainerThisInstallationOwnsAndNoLiveSandboxReferences()
    {
        var (provider, client, nodeRoot) = CreateProvider();
        var orphan = await SeedContainerAsync(client, DockerSandboxRuntimeProvider.BuildInstallId(nodeRoot));

        var removed = await provider.SweepOrphanedContainersAsync();

        AssertEx.Equal(expected: 1, removed);
        AssertEx.Contains(client.RemovedContainerIds, orphan);
    }

    [Test]
    public async Task Sweep_LeavesAnotherInstallationsContainerAlone()
    {
        // The case the install label exists for. The owner label's value is the constant "development", so every XE
        // installation pointed at one daemon carries it — a sweep keyed on it alone would remove a second
        // installation's LIVE Development Mode container while its engine was using it.
        var (provider, client, nodeRoot) = CreateProvider();
        var ours = await SeedContainerAsync(client, DockerSandboxRuntimeProvider.BuildInstallId(nodeRoot));
        var theirs = await SeedContainerAsync(client, DockerSandboxRuntimeProvider.BuildInstallId(Path.Combine(Path.GetTempPath(), "some-other-install")));

        var removed = await provider.SweepOrphanedContainersAsync();

        AssertEx.Equal(expected: 1, removed);
        AssertEx.Contains(client.RemovedContainerIds, ours);
        AssertEx.False(client.RemovedContainerIds.Contains(theirs, StringComparer.Ordinal), "another installation's container was removed");
    }

    [Test]
    public async Task Sweep_LeavesAContainerWithNoEngineLabelsAlone()
    {
        var (provider, client, _) = CreateProvider();
        var foreign = await SeedContainerAsync(client,
            labels: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["com.example.someone-else"] = "yes"
            });

        var removed = await provider.SweepOrphanedContainersAsync();

        AssertEx.Equal(expected: 0, removed);
        AssertEx.False(client.RemovedContainerIds.Contains(foreign, StringComparer.Ordinal), "a container carrying no engine label was removed");
    }

    [Test]
    public async Task Sweep_LeavesThisProcessesOwnLiveSandboxAlone()
    {
        var (provider, client, nodeRoot) = CreateProvider();
        var handle = await provider.CreateOrAttachAsync(CreateRequest(nodeRoot));
        var live = client.CreatedContainerIds[0];
        var orphan = await SeedContainerAsync(client, DockerSandboxRuntimeProvider.BuildInstallId(nodeRoot));

        var removed = await provider.SweepOrphanedContainersAsync();

        AssertEx.Equal(expected: 1, removed);
        AssertEx.Contains(client.RemovedContainerIds, orphan);
        AssertEx.False(client.RemovedContainerIds.Contains(live, StringComparer.Ordinal), "the live sandbox's container was removed");
        // And the handle still works, which is the property the assertion above is a proxy for.
        AssertEx.Equal(handle.SandboxId, (await provider.ConnectAsync(handle.AttachKey)).SandboxId);
    }

    [Test]
    public async Task Sweep_WhenOneRemovalFails_KeepsGoing()
    {
        // A container the daemon will not remove is a leak an operator has to clear by hand. It must not also cost
        // them every other orphan on the daemon.
        var (provider, client, nodeRoot) = CreateProvider();
        var installId = DockerSandboxRuntimeProvider.BuildInstallId(nodeRoot);
        var stubborn = await SeedContainerAsync(client, installId);
        var collectable = await SeedContainerAsync(client, installId);

        client.RemovalFailure = containerId => string.Equals(containerId, stubborn, StringComparison.Ordinal)
            ? new DockerRuntimeException(DockerDaemonPreflightStatus.ProbeFailed, "device or resource busy")
            : null;

        var removed = await provider.SweepOrphanedContainersAsync();

        AssertEx.Equal(expected: 1, removed);
        AssertEx.Contains(client.RemovedContainerIds, collectable);
        AssertEx.False(client.RemovedContainerIds.Contains(stubborn, StringComparer.Ordinal), "a removal that threw was recorded as done");
    }

    [Test]
    public async Task Sweep_IsIdempotent()
    {
        var (provider, client, nodeRoot) = CreateProvider();
        await SeedContainerAsync(client, DockerSandboxRuntimeProvider.BuildInstallId(nodeRoot));

        AssertEx.Equal(expected: 1, await provider.SweepOrphanedContainersAsync());
        AssertEx.Equal(expected: 0, await provider.SweepOrphanedContainersAsync());
    }

    [Test]
    public void BuildInstallId_IsStableForOneDirectoryAndDifferentForAnother()
    {
        // Stability across restarts is the whole premise: an id that moved would orphan every container the previous
        // run created, permanently.
        var root = Path.Combine(Path.GetTempPath(), "xe-install-id", "node");

        AssertEx.Equal(DockerSandboxRuntimeProvider.BuildInstallId(root),
            DockerSandboxRuntimeProvider.BuildInstallId(root + Path.DirectorySeparatorChar));
        AssertEx.NotEqual(DockerSandboxRuntimeProvider.BuildInstallId(root), DockerSandboxRuntimeProvider.BuildInstallId(root + "-other"));

        // Hashed, not the path: a container label is readable by anyone who can list containers on that daemon, and
        // this path routinely carries the operator's account name.
        AssertEx.False(DockerSandboxRuntimeProvider.BuildInstallId(root).Contains("node", StringComparison.Ordinal),
            "the install id leaks the node data directory path");
    }

    /// <summary>
    ///     Creates a container through the fake exactly as the provider would, so the labels under test are the ones
    ///     <see cref="DockerSandboxHardening.BuildSpecification" /> really emits rather than a hand-written copy.
    /// </summary>
    private static async Task<string> SeedContainerAsync(FakeDockerRuntimeClient client, string installId)
    {
        var sandboxId = Guid.NewGuid().ToString("N")[..32];
        var specification = DockerSandboxHardening.BuildSpecification(DockerSandboxHardeningTests.Options(),
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "xe-dev-" + sandboxId,
            sandboxId,
            installId,
            [DockerSandboxHardeningTests.Mount()]);

        return await client.CreateContainerAsync(specification);
    }

    private static async Task<string> SeedContainerAsync(FakeDockerRuntimeClient client, IReadOnlyDictionary<string, string> labels)
    {
        var specification = DockerSandboxHardening.BuildSpecification(DockerSandboxHardeningTests.Options(),
            new ResolvedContainerIdentity(UserId: 1000, GroupId: 1000),
            "someone-elses-container",
            "sandbox-x",
            "install-x",
            [DockerSandboxHardeningTests.Mount()]) with
        {
            Labels = labels
        };

        return await client.CreateContainerAsync(specification);
    }

    private static (DockerSandboxRuntimeProvider Provider, FakeDockerRuntimeClient Client, string NodeRoot) CreateProvider()
    {
        var nodeRoot = Path.Combine(Path.GetTempPath(), "xe-container-sweep-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(nodeRoot);

        var client = new FakeDockerRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///fake.sock"),
            DockerDaemonEndpointSource.Configuration));

        var provider = new DockerSandboxRuntimeProvider(new StaticOptionsMonitor<ContainerSandboxOptions>(DockerSandboxHardeningTests.Options()),
            new SweepClientFactory(client),
            new FakeNodeDataDirectory(nodeRoot),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        return (provider, client, nodeRoot);
    }

    private static SandboxCreateRequest CreateRequest(string workspaceRoot)
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner-1",
                NodeId = "node-1",
                ProviderName = DockerSandboxRuntimeProvider.Name,
                RuntimeProfile = "development",
                ManifestVersion = 1
            },
            RuntimeProfile = "development",
            NetworkPolicy = SandboxNetworkPolicy.None,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspaceRoot
            }
        };
    }

    private sealed class SweepClientFactory : IDockerRuntimeClientFactory
    {
        private readonly FakeDockerRuntimeClient _client;

        public SweepClientFactory(FakeDockerRuntimeClient client)
        {
            _client = client;
        }

        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint)
        {
            return _client;
        }
    }
}
