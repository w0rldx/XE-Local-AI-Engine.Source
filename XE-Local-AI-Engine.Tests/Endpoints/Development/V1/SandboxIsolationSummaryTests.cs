namespace XE_Local_AI_Engine.Tests.Endpoints.Development.V1;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.ContainerSandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The operator-facing isolation level, derived by <see cref="DevelopmentContractMapper.ToIsolationSummary" />.
///     <para>
///         Every case here drives a REAL provider rather than a hand-written flag set, because the value of this
///         projection is precisely that it cannot claim a boundary the provider does not advertise. A test that fed the
///         mapper its own booleans would pass while the providers said something else, which is the one failure this
///         surface exists to prevent.
///     </para>
/// </summary>
public sealed class SandboxIsolationSummaryTests
{
    [Test]
    public void ToIsolationSummary_WhenTheHostCannotContainAnything_ReportsNoneAndTheMeasuredReason()
    {
        const string Reason = "the host is not Linux (the Windows Job Object path is not implemented)";
        var containment = SandboxContainment.None with
        {
            FilesystemIsolationUnavailableReason = Reason
        };
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("agent-home", provider, containment);

        AssertEx.Equal("agent-home", summary.Role);
        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, summary.Provider);
        AssertEx.Equal("process", summary.Backend);
        AssertEx.Equal("None", summary.Level);
        AssertEx.False(summary.FilesystemIsolation);
        AssertEx.False(summary.NetworkIsolation);
        AssertEx.False(summary.ResourceLimits);
        AssertEx.False(summary.ReadOnlyMounts);

        // The measured reason, not a generic "unavailable": on a Windows host this sentence is the whole explanation an
        // operator gets for why run_in_agent_home is unisolated.
        AssertEx.Equal(Reason, summary.FilesystemIsolationUnavailableReason);
    }

    [Test]
    public void ToIsolationSummary_WhenEveryMechanismIsActive_ReportsIsolatedOverBwrap()
    {
        var containment = FullyContainedHost();
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("development", provider, containment);

        AssertEx.Equal("bwrap", summary.Backend);
        AssertEx.Equal("Isolated", summary.Level);
        AssertEx.True(summary.FilesystemIsolation);
        AssertEx.True(summary.NetworkIsolation);
        AssertEx.True(summary.ResourceLimits);
        AssertEx.Null(summary.FilesystemIsolationUnavailableReason);
    }

    [Test]
    public void ToIsolationSummary_WhenOnlySomeMechanismsAreActive_ReportsConfined()
    {
        // A real host shape: systemd user scopes and empty network namespaces work, but bwrap does not.
        var containment = FullyContainedHost() with
        {
            FilesystemIsolation = null,
            FilesystemIsolationUnavailableReason = "bwrap is not installed"
        };
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("agent-home", provider, containment);

        AssertEx.Equal("Confined", summary.Level);
        AssertEx.Equal("process", summary.Backend);
        AssertEx.Equal("bwrap is not installed", summary.FilesystemIsolationUnavailableReason);
    }

    [Test]
    public async Task ToIsolationSummary_ForTheContainerProviderOnTheDevelopmentRole_ReportsIsolated()
    {
        // The fix this test's predecessor asked for landed in the provider, as it said it should. A hardened container
        // DOES have a filesystem boundary — read-only rootfs, engine-generated mounts only, no host namespaces, every
        // setting read back and fail-closed on mismatch — and it now advertises
        // SupportsHostFilesystemBoundary to say so. What it still does not advertise is
        // SupportsFilesystemIsolation, which means "serves SandboxIsolationMode.Filesystem", the bubblewrap chain's
        // own create-request contract; this projection reads the property, not that mechanism, which is what its own
        // FilesystemIsolation contract says ("the host filesystem absent from its mount namespace").
        await using var provider = CreateDockerProvider();

        var summary = DevelopmentContractMapper.ToIsolationSummary("development", provider, FullyContainedHost());

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, summary.Provider);
        AssertEx.Equal("docker", summary.Backend);
        AssertEx.Equal("Isolated", summary.Level);
        AssertEx.True(summary.NetworkIsolation);
        AssertEx.True(summary.ResourceLimits);
        AssertEx.True(summary.ReadOnlyMounts);
        AssertEx.True(summary.FilesystemIsolation);
        AssertEx.Null(summary.FilesystemIsolationUnavailableReason);

        // The narrower mechanism flag is still absent, and must stay absent: the provider refuses
        // SandboxIsolationMode.Filesystem on a create request, and advertising it would make that refusal a surprise.
        AssertEx.False(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation));
    }

    [Test]
    public void ToIsolationSummary_ForTheDeterministicFake_ReportsNoneWhateverTheHostCanDo()
    {
        var summary = DevelopmentContractMapper.ToIsolationSummary("work-session",
            new FakeSandboxRuntimeProvider(TimeProvider.System),
            FullyContainedHost());

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, summary.Provider);
        AssertEx.Equal("none", summary.Backend);
        AssertEx.Equal("None", summary.Level);
        AssertEx.Equal("the deterministic in-memory provider has no mount namespace and never will",
            summary.FilesystemIsolationUnavailableReason);
    }

    private static SandboxContainment FullyContainedHost()
    {
        return new SandboxContainment
        {
            SupportsProcessGroup = true,
            SupportsResourceLimits = true,
            SupportsNetworkIsolation = true,
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            UnsharePath = "/usr/bin/unshare",
            EnvPath = "/usr/bin/env",
            FilesystemIsolation = new SandboxFilesystemIsolation
            {
                SetsidPath = "/usr/bin/setsid",
                SystemdRunPath = "/usr/bin/systemd-run",
                SystemctlPath = "/usr/bin/systemctl",
                BwrapPath = "/usr/bin/bwrap",
                UsrMergeEntries = [],
                UserId = 1000,
                GroupId = 1000,
                UserBusEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["XDG_RUNTIME_DIR"] = "/run/user/1000"
                }
            }
        };
    }

    private static ProcessSandboxRuntimeProvider CreateProcessProvider(SandboxContainment containment)
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new StubProbe(containment)));
    }

    private static DockerSandboxRuntimeProvider CreateDockerProvider()
    {
        // The node data directory is only ever read when a container is created; this projection reads Capabilities,
        // which contacts nothing and touches no path, so a name that is never resolved is the honest argument here.
        return new DockerSandboxRuntimeProvider(new StaticOptionsMonitor<ContainerSandboxOptions>(DockerSandboxHardeningTests.Options()),
            new StubClientFactory(),
            new FakeNodeDataDirectory(Path.Combine(Path.GetTempPath(), "xe-isolation-summary-tests")),
            TimeProvider.System,
            NullLogger<DockerSandboxRuntimeProvider>.Instance);
    }

    private sealed class StubProbe : ISandboxContainmentProbe
    {
        public StubProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }

    // Capabilities never reaches a daemon; the factory exists only so the provider can be constructed.
    private sealed class StubClientFactory : IDockerRuntimeClientFactory
    {
        public IDockerRuntimeClient Create(DockerDaemonEndpoint endpoint) =>
            new FakeDockerRuntimeClient(endpoint);
    }
}
