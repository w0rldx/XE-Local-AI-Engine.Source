namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The CONTRACT half of the filesystem boundary, asserted without starting anything: that the capability is
///     independent of the others, that asking for it on a host without it is refused rather than downgraded, and —
///     the assertion that protects every existing caller — that a request which does not mention it produces exactly
///     the chain it produced before this mode existed.
/// </summary>
public sealed class SandboxFilesystemIsolationContractTests
{
    [Test]
    public async Task ContainmentProbe_WhenTheFilesystemProbeThrows_KeepsTheResourceAndNetworkResults()
    {
        // The reason the filesystem probe is caught separately. It does far more than the other two — descriptors,
        // memory files, five namespaces, a shell script — so it has far more ways to fail, and a shared guard would
        // let one of those failures silently withdraw ceilings and egress denial that were measured successfully.
        var reference = new HostSandboxContainmentProbe().Containment;
        var withFailingFilesystemProbe = new HostSandboxContainmentProbe(logger: null,
            _ => throw new InvalidOperationException("the probe exploded")).Containment;

        AssertEx.False(withFailingFilesystemProbe.SupportsFilesystemIsolation);
        AssertEx.NotNullOrEmpty(withFailingFilesystemProbe.FilesystemIsolationUnavailableReason);
        AssertEx.Contains(withFailingFilesystemProbe.FilesystemIsolationUnavailableReason, "the probe exploded");

        AssertEx.Equal(reference.SupportsResourceLimits, withFailingFilesystemProbe.SupportsResourceLimits);
        AssertEx.Equal(reference.SupportsNetworkIsolation, withFailingFilesystemProbe.SupportsNetworkIsolation);
        AssertEx.Equal(reference.SupportsProcessGroup, withFailingFilesystemProbe.SupportsProcessGroup);
        AssertEx.Equal<string?>(reference.ResourceLimitsUnavailableReason, withFailingFilesystemProbe.ResourceLimitsUnavailableReason);
        AssertEx.Equal<string?>(reference.NetworkIsolationUnavailableReason, withFailingFilesystemProbe.NetworkIsolationUnavailableReason);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ANonIsolatedRequest_ProducesTheExactChainItProducedBeforeThisModeExisted()
    {
        // The regression guard for every existing consumer. AgentHome, Coder and Development Mode all create sandboxes
        // without naming an isolation mode, and their launch chain must be byte-identical to the pre-existing one.
        var containment = new SandboxContainment
        {
            SupportsProcessGroup = true,
            SupportsResourceLimits = true,
            SupportsNetworkIsolation = true,
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            UnsharePath = "/usr/bin/unshare",
            EnvPath = "/usr/bin/env"
        };
        var policy = new SandboxLaunchPolicy
        {
            ResourceLimits = new SandboxResourceLimits { MemoryMb = 512 },
            DenyNetworkEgress = true
        };

        var descriptor = SandboxLaunchPlan.Create("/bin/echo", ["hello"], policy, containment);

        AssertEx.Equal("/usr/bin/setsid", descriptor.FileName);
        AssertEx.Equal(string.Join(' ',
                "/usr/bin/systemd-run --scope --user -q -p MemoryMax=512M -p MemorySwapMax=0 --",
                "/usr/bin/unshare --user --map-current-user --net --",
                "/usr/bin/env -u XDG_RUNTIME_DIR -u DBUS_SESSION_BUS_ADDRESS --",
                "/bin/echo hello"),
            string.Join(' ', descriptor.Arguments));
        AssertEx.False(descriptor.AppliedFilesystemIsolation);
        AssertEx.Null(descriptor.ScopeUnitName);
        AssertEx.Null(descriptor.LaunchResources);

        await Task.CompletedTask;
    }

    [Test]
    public async Task CreateOrAttach_RefusesFilesystemIsolation_OnAHostThatCannotDeliverIt()
    {
        using var provider = CreateProvider(new StubProbe(SandboxContainment.None with
        {
            FilesystemIsolationUnavailableReason = "no root-owned bwrap was found"
        }));

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() =>
            provider.CreateOrAttachAsync(IsolatedRequest()));

        // The measured reason travels with the refusal: a caller that has to decide whether to fall back needs to
        // know WHY, and an operator reading a log needs to know what to install.
        AssertEx.Contains(exception.Message, "no root-owned bwrap was found");

        await Task.CompletedTask;
    }

    [Test]
    public async Task CreateOrAttach_RefusesReadOnlyTrees_WithoutFilesystemIsolation()
    {
        using var provider = CreateProvider(new StubProbe(SandboxContainment.None));

        var exception = await AssertEx.ThrowsAsync<SandboxCapabilityNotSupportedException>(() =>
            provider.CreateOrAttachAsync(IsolatedRequest() with
            {
                Isolation = SandboxIsolationMode.None,
                ReadOnlyTrees = ["/opt/xe/venv"]
            }));

        AssertEx.Contains(exception.Message, "read-only trees");

        await Task.CompletedTask;
    }

    [Test]
    public async Task Capabilities_AdvertiseTheFilesystemBoundary_IfAndOnlyIfTheProbeMeasuredIt()
    {
        using var without = CreateProvider(new StubProbe(SandboxContainment.None));
        AssertEx.False(without.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation));

        using var with = CreateProvider(new StubProbe(SandboxContainment.None with { FilesystemIsolation = FakeIsolation() }));
        AssertEx.True(with.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation));

        await Task.CompletedTask;
    }

    [Test]
    public async Task CreateRequest_RefusesANonPositiveThreadLimit()
    {
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = IsolatedRequest() with { ThreadLimit = 0 });
        AssertEx.Throws<ArgumentOutOfRangeException>(() => _ = IsolatedRequest() with { ThreadLimit = -1 });

        await Task.CompletedTask;
    }

    [Test]
    public async Task CanBindReadOnlyTree_RefusesEveryMountPointTheChainOwns()
    {
        // Two different failure shapes hide behind this one rule: a tree under /usr or /proc cannot be mounted at all,
        // while a tree under /tmp or /work is mounted and then silently shadowed by the jail. The second is the
        // dangerous one, and it is why the rule rejects rather than reorders.
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/tmp/xe-agent-home-sandboxes/tree"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/work/tree"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/usr/lib/xe"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/proc"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/lib64/xe"));
        AssertEx.False(SandboxIsolatedChain.CanBindReadOnlyTree("/"));

        AssertEx.True(SandboxIsolatedChain.CanBindReadOnlyTree("/home/user/.local/share/xe/compute-runtime/venv"));
        AssertEx.True(SandboxIsolatedChain.CanBindReadOnlyTree("/var/lib/xe/venv"));
        AssertEx.True(SandboxIsolatedChain.CanBindReadOnlyTree("/opt/xe/venv"));

        await Task.CompletedTask;
    }

    private static SandboxCreateRequest IsolatedRequest()
    {
        return new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "compute",
                ManifestVersion = 1
            },
            RuntimeProfile = "compute",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            Isolation = SandboxIsolationMode.Filesystem
        };
    }

    private static SandboxFilesystemIsolation FakeIsolation()
    {
        return new SandboxFilesystemIsolation
        {
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            SystemctlPath = "/usr/bin/systemctl",
            BwrapPath = "/usr/bin/bwrap",
            UsrMergeEntries = [],
            UserId = 1000,
            GroupId = 1000,
            UserBusEnvironment = new Dictionary<string, string>(StringComparer.Ordinal) { ["XDG_RUNTIME_DIR"] = "/run/user/1000" }
        };
    }

    private static ProcessSandboxRuntimeProvider CreateProvider(ISandboxContainmentProbe probe)
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions
            {
                MaxCopyFileBytes = LocalContainerOptions.DefaultMaxCopyFileBytes,
                MaxJailDiskBytes = LocalContainerOptions.DefaultMaxJailDiskBytes
            }),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(probe));
    }

    private sealed class StubProbe : ISandboxContainmentProbe
    {
        public StubProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }
}
