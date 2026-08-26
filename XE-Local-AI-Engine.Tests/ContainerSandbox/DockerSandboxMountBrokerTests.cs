namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The container half of the mount broker, driven through the fake Docker client.
///     <para>
///         Two properties matter here and neither is "the mount was passed along". First, the engine's mounts must flow
///         through the same requested set used to verify the daemon's read-back — a broker that
///         composed its own list would route them around that check while leaving it looking intact. Second, the
///         overlap sweep has to be N-way: with the workspace, the scratch tmpfs and four runtime mounts there is no
///         fixed number of pairwise comparisons that covers it.
///     </para>
/// </summary>
public sealed class DockerSandboxMountBrokerTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 7, day: 30, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-docker-mount-broker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public async Task CreateOrAttachAsync_BindsEveryRequestedMountAndReportsItsContainerPath()
    {
        var (provider, _, workspace) = CreateProvider();
        var home = CreateDirectory("runtime/home");

        var handle = await provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = home,
                SandboxPath = "/xe-runtime/home",
                ReadOnly = false
            }
        ]));

        AssertEx.Equal("/xe-runtime/home", handle.TryResolveSandboxPath(home));
        AssertEx.Equal("/xe-runtime/home/.nuget", handle.TryResolveSandboxPath(Path.Combine(home, ".nuget")));
    }

    [Test]
    public async Task CreateOrAttachAsync_DerivesTheTargetForAMountInsideTheWorkspaceRatherThanTrustingTheRequest()
    {
        // The engine must be able to ask for a nested mount without knowing what the workspace is called inside a
        // container — that is Docker-shaped knowledge the neutral contract forbids it from holding. So a host path
        // under the trusted workspace is placed by derivation, and the requested spelling is not consulted.
        var (provider, _, workspace) = CreateProvider();
        var config = CreateFile(Path.Combine(workspace, ".git", "config"), "[core]\n\trepositoryformatversion = 0\n");

        var handle = await provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = config,
                SandboxPath = "/.git/config",
                ReadOnly = true
            }
        ]));

        AssertEx.Equal("/workspace/.git/config", handle.TryResolveSandboxPath(config));
        AssertEx.Contains(handle.Mounts, mount => string.Equals(mount.SandboxPath, "/workspace/.git/config", StringComparison.Ordinal) && mount.ReadOnly);
    }

    [Test]
    public async Task CreateOrAttachAsync_TheEnginesOwnMountsGoThroughTheSameRequestedSetTheD7VerificationChecks()
    {
        // If the broker's mounts bypassed FindViolations' requested-set comparison, an INJECTED mount would still be
        // caught (the test above this file's sibling covers that) but the engine's own would be unverifiable. Proven
        // here by making the daemon drop one of the broker's mounts: the create must fail closed.
        var (provider, client, workspace) = CreateProvider();
        var home = CreateDirectory("verified/home");
        client.SettingsMutator = settings => settings with
        {
            Mounts = [.. settings.Mounts.Where(static mount => !string.Equals(mount.ContainerPath, "/xe-runtime/home", StringComparison.Ordinal))]
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = home,
                SandboxPath = "/xe-runtime/home",
                ReadOnly = false
            }
        ])));

        AssertEx.Contains(exception.Message, "/xe-runtime/home");
        AssertEx.Contains(exception.Message, "absent");
        AssertEx.Contains(client.RemovedContainerIds, client.CreatedContainerIds[0]);
    }

    [Test]
    public async Task CreateOrAttachAsync_WhenTheDaemonServedAReadOnlyMountWritable_Refuses()
    {
        var (provider, client, workspace) = CreateProvider();
        var config = CreateFile(Path.Combine(workspace, ".git", "config"), "[core]\n");
        client.SettingsMutator = settings => settings with
        {
            Mounts =
            [
                .. settings.Mounts.Select(static mount => mount with
                {
                    ReadOnly = false
                })
            ]
        };

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = config,
                SandboxPath = "/.git/config",
                ReadOnly = true
            }
        ])));

        AssertEx.Contains(exception.Message, "read-only and is writable");
    }

    [Test]
    public async Task CreateOrAttachAsync_RefusesADirectoryMountThatShadowsAnotherMount()
    {
        // The N-way case: this pair is neither (workspace, scratch) nor (workspace, mount) — it is the THIRD pair, the
        // one a fixed set of pairwise calls omits.
        var (provider, _, workspace) = CreateProvider();
        var outer = CreateDirectory("nested/outer");
        var inner = CreateDirectory("nested/outer/inner");

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = outer,
                SandboxPath = "/xe-runtime",
                ReadOnly = false
            },
            new SandboxMount
            {
                HostPath = inner,
                SandboxPath = "/xe-runtime/home",
                ReadOnly = false
            }
        ])));

        AssertEx.Contains(exception.Message, "overlap");
    }

    [Test]
    public async Task CreateOrAttachAsync_RefusesAMountThatShadowsTheScratchFilesystem()
    {
        var (provider, _, workspace) = CreateProvider();
        var home = CreateDirectory("scratch-clash/home");

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = home,
                SandboxPath = "/scratch/home",
                ReadOnly = false
            }
        ])));

        AssertEx.Contains(exception.Message, "overlap");
    }

    [Test]
    public async Task CreateOrAttachAsync_RefusesAMountSourceTheEngineDidNotCreate()
    {
        var (provider, _, workspace) = CreateProvider();

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = Path.Combine(_root, "never-created"),
                SandboxPath = "/xe-runtime/home",
                ReadOnly = false
            }
        ])));

        AssertEx.Contains(exception.Message, "does not exist");
    }

    [Test]
    public async Task CreateOrAttachAsync_RefusesARelativeOrEscapingMountTarget()
    {
        var (provider, _, workspace) = CreateProvider();
        var home = CreateDirectory("bad-target/home");

        foreach (var target in new[]
                 {
                     "xe-runtime/home",
                     "/xe-runtime/../../etc",
                     "/"
                 })
        {
            var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() => provider.CreateOrAttachAsync(Request(workspace, [
                new SandboxMount
                {
                    HostPath = home,
                    SandboxPath = target,
                    ReadOnly = false
                }
            ])));

            AssertEx.Contains(exception.Message, "absolute in-container path");
        }
    }

    [Test]
    public async Task CreateOrAttachAsync_AllowsAFileMountNestedInsideTheWorkspaceMount()
    {
        // The one legitimate nesting, and the reason the sweep is not a flat "no overlaps": a FILE mount replaces
        // exactly one path and can hide nothing else, which is what makes .git/config read-only while the work tree
        // and .git/index stay writable.
        var (provider, _, workspace) = CreateProvider();
        var config = CreateFile(Path.Combine(workspace, ".git", "config"), "[core]\n");

        var handle = await provider.CreateOrAttachAsync(Request(workspace, [
            new SandboxMount
            {
                HostPath = config,
                SandboxPath = "/.git/config",
                ReadOnly = true
            }
        ]));

        AssertEx.Equal(expected: 2, handle.Mounts.Count);
    }

    [Test]
    public void FindOverlap_SweepsEveryPairRatherThanTheFirstTwo()
    {
        // Directly, because the sweep is the part that stops being correct when a third target is added by hand: the
        // colliding pair here is (second, third), which no (first, N) comparison reaches.
        AssertEx.Null(ContainerSandboxOptionsValidator.FindOverlap([
            new ContainerMountTarget("a", "/workspace"), new ContainerMountTarget("b", "/scratch"), new ContainerMountTarget("c", "/xe-runtime")
        ]));

        var collision = ContainerSandboxOptionsValidator.FindOverlap([
            new ContainerMountTarget("a", "/workspace"), new ContainerMountTarget("b", "/xe-runtime"), new ContainerMountTarget("c", "/xe-runtime/home")
        ]);

        AssertEx.NotNull(collision, "the third pair was not swept.");
        AssertEx.Equal("b", collision!.First.Name);
        AssertEx.Equal("c", collision.Second.Name);
    }

    private (DockerSandboxRuntimeProvider Provider, FakeDockerRuntimeClient Client, string Workspace) CreateProvider()
    {
        var workspace = CreateDirectory("workspace");
        var client = new FakeDockerRuntimeClient(new DockerDaemonEndpoint(new Uri("unix:///fake.sock"),
            DockerDaemonEndpointSource.Configuration));

        var provider = new DockerSandboxRuntimeProvider(new StaticOptionsMonitor<ContainerSandboxOptions>(DockerSandboxHardeningTests.Options()),
            new SingleClientFactory(client),
            new FakeNodeDataDirectory(workspace),
            new FixedTimeProvider(FixedNow),
            NullLogger<DockerSandboxRuntimeProvider>.Instance);

        return (provider, client, workspace);
    }

    private static SandboxCreateRequest Request(string workspace, IReadOnlyList<SandboxMount>? mounts)
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = Guid.NewGuid().ToString("N"),
                NodeId = "node-1",
                ProviderName = DockerSandboxRuntimeProvider.Name,
                RuntimeProfile = "development",
                ManifestVersion = 1
            },
            RuntimeProfile = "development",
            NetworkPolicy = SandboxNetworkPolicy.None,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = workspace
            },
            Mounts = mounts
        };
    }

    private string CreateDirectory(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetFullPath(path);
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
