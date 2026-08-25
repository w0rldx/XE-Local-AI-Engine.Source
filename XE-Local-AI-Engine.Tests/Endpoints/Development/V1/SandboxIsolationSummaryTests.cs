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
///         Every case here drives a REAL provider rather than a hand-written flag set, and pairs it with a REAL
///         <see cref="SandboxWorkloads" /> declaration rather than an invented one. Both halves matter: the projection
///         must not claim a boundary the provider does not advertise, and — the failure this file's newer cases pin —
///         it must not claim one the ROLE never asked for. A test that fed the mapper its own booleans, or its own
///         requirements record, would pass while the shipped constants said something else.
///     </para>
/// </summary>
public sealed class SandboxIsolationSummaryTests
{
    [Test]
    public void ToIsolationSummary_WhenTheHostCannotContainAnything_ReportsNone()
    {
        const string Reason = "the host is not Linux (the Windows Job Object path is not implemented)";
        var containment = SandboxContainment.None with
        {
            FilesystemIsolationUnavailableReason = Reason
        };
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("agent-home",
            SandboxWorkloads.AgentHome,
            provider,
            containment);

        AssertEx.Equal("agent-home", summary.Role);
        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name, summary.Provider);
        AssertEx.Equal("process", summary.Backend);
        AssertEx.Equal("None", summary.Level);
        AssertEx.False(summary.FilesystemIsolation);
        AssertEx.False(summary.NetworkIsolation);
        AssertEx.False(summary.ResourceLimits);
        AssertEx.False(summary.ReadOnlyMounts);

        // AgentHome declares an isolation floor of None, so the honest sentence is that the role never asked — even
        // on a host that also could not have served it. The probe reason belongs to the role that DOES ask; see
        // ToIsolationSummary_ForRunPythonOnAHostWithoutABoundary_ReportsTheMeasuredProbeReason.
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, "not requested by this role");
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, SandboxWorkloads.AgentHome.Workload);
        AssertEx.False(summary.FilesystemIsolationUnavailableReason?.Contains(Reason, StringComparison.Ordinal) == true);

        // The ceilings axis reads the other way round now that every executing role asks: AgentHome DOES request
        // them, so its "No" here is the host's, and the sentence is the measured probe reason rather than
        // "not requested". Telling those two apart is the whole point of the pair.
        AssertEx.True(SandboxWorkloads.AgentHome.RequestsResourceLimits);
        AssertEx.False(summary.ResourceLimitsUnavailableReason?.Contains("not requested by this role", StringComparison.Ordinal) == true);
        AssertEx.Contains(summary.ResourceLimitsUnavailableReason, "did not advertise resource ceilings");

        // Egress is neither served nor required on this node: the switch is off, so denial is a best-effort
        // tightening the host cannot honour, not a precondition that would refuse the run.
        AssertEx.False(summary.NetworkIsolationRequired);
    }

    /// <summary>
    ///     The <c>RequireEgressDenial</c> switch is what the panel renders as "required" rather than "where available",
    ///     and it changes NOTHING else about the row — the served columns still report what the host can do. On this
    ///     host that combination (required and not served) is exactly the node whose AgentHome runs will refuse to
    ///     prepare, which is the state an operator most needs to be able to read off the table.
    /// </summary>
    [Test]
    public void ToIsolationSummary_WhenTheNodeRequiresEgressDenial_ReportsItAsRequiredEvenWhereUnserved()
    {
        var containment = SandboxContainment.None;
        using var provider = CreateProcessProvider(containment);

        var served = DevelopmentContractMapper.ToIsolationSummary("agent-home",
            SandboxWorkloads.AgentHome,
            provider,
            containment,
            nodeRequiresEgressDenial: true);

        AssertEx.True(served.NetworkIsolationRequired);
        AssertEx.False(served.NetworkIsolation);
    }

    /// <summary>
    ///     <c>run_python</c> needs no switch: its own declaration will not accept egress, so denial is a precondition
    ///     for it on every node. Derived from <see cref="SandboxRequirements.NetworkFloor" /> rather than restated, so
    ///     a role that stops declaring the floor stops being reported as requiring it.
    /// </summary>
    [Test]
    public void ToIsolationSummary_ForRunPython_ReportsEgressDenialRequiredWithoutTheSwitch()
    {
        var containment = FullyContainedHost();
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("run_python",
            SandboxWorkloads.RunPython,
            provider,
            containment);

        AssertEx.True(summary.NetworkIsolationRequired);
        AssertEx.False(SandboxWorkloads.AgentHome.NetworkFloor == SandboxWorkloads.RunPython.NetworkFloor);
    }

    /// <summary>
    ///     <c>run_python</c> is the ONE role that declares <see cref="SandboxIsolationMode.Filesystem" />, so on a host
    ///     whose bubblewrap chain the probe exercised it is the one role that reaches <c>Isolated</c> over <c>bwrap</c>.
    /// </summary>
    [Test]
    public void ToIsolationSummary_ForRunPythonOnAFullyContainedHost_ReportsIsolatedOverBwrap()
    {
        var containment = FullyContainedHost();
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("run_python",
            SandboxWorkloads.RunPython,
            provider,
            containment);

        AssertEx.Equal("bwrap", summary.Backend);
        AssertEx.Equal("Isolated", summary.Level);
        AssertEx.True(summary.FilesystemIsolation);
        AssertEx.True(summary.NetworkIsolation);
        AssertEx.True(summary.ResourceLimits);
        AssertEx.Null(summary.FilesystemIsolationUnavailableReason);
        AssertEx.Null(summary.ResourceLimitsUnavailableReason);
    }

    [Test]
    public void ToIsolationSummary_ForMcpStdioOnAFullyContainedHost_ReportsIsolatedAndBounded()
    {
        // A Sandboxed stdio MCP server declares the same filesystem floor run_python does, so its boundary is the
        // same — and since the 2026-08-25 ceilings ruling it is bounded too, on the HOST-TOOLCHAIN profile rather than
        // run_python's: it is a long-lived operator-installed program, so it needs a build-sized ceiling, not a
        // script-sized one that would strangle a language server on its first index.
        var containment = FullyContainedHost();
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("mcp-stdio",
            SandboxWorkloads.McpStdio,
            provider,
            containment);

        AssertEx.Equal("mcp-stdio", summary.Role);
        AssertEx.Equal("bwrap", summary.Backend);
        AssertEx.True(summary.FilesystemIsolation);
        AssertEx.True(summary.NetworkIsolation);
        AssertEx.Null(summary.FilesystemIsolationUnavailableReason);

        AssertEx.Equal(SandboxCeilingProfile.HostToolchain, SandboxWorkloads.McpStdio.Ceilings);
        AssertEx.True(summary.ResourceLimits);
        AssertEx.Null(summary.ResourceLimitsUnavailableReason);

        // Denial is a precondition here without any node switch: the declaration's own network floor refuses egress,
        // which is what the panel renders as "required" rather than "where available".
        AssertEx.True(summary.NetworkIsolationRequired);
    }

    [Test]
    public void ToIsolationSummary_ForMcpStdioOnAHostWithoutABoundary_ReportsTheMeasuredProbeReason()
    {
        // The row an operator reads BEFORE registering a server. On this host every Sandboxed registration will refuse
        // to connect, and the panel has to say why ahead of the first failed connection rather than only after it.
        const string Reason = "the host is not Linux (the Windows Job Object path is not implemented)";
        var containment = SandboxContainment.None with
        {
            FilesystemIsolationUnavailableReason = Reason
        };
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("mcp-stdio",
            SandboxWorkloads.McpStdio,
            provider,
            containment);

        AssertEx.False(summary.FilesystemIsolation);
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, Reason);
        AssertEx.False(summary.FilesystemIsolationUnavailableReason?.Contains("not requested by this role", StringComparison.Ordinal) == true,
            "this role DOES ask, so the honest sentence is the measured host reason, not 'it never asked'.");
    }

    /// <summary>
    ///     THE REGRESSION THIS FILE EXISTS FOR SINCE G6b. Live on a Linux box with a working chain, the panel showed
    ///     role <c>development</c> as filesystem-isolated and <c>Isolated</c> — read straight off the process backend's
    ///     advertised flags. It is false: <see cref="SandboxWorkloads.DevelopmentModeHostToolchain" /> declares
    ///     <see cref="SandboxIsolationMode.None" />, and Development Mode runs the host toolchain with the worktree
    ///     mounted. Same provider, same host, same call as the <c>run_python</c> case above — only the declaration
    ///     differs, and the served posture differs with it.
    /// </summary>
    [Test]
    public void ToIsolationSummary_ForDevelopmentOnAFullyContainedHost_ReportsConfinedBecauseTheRoleAsksForNoBoundary()
    {
        var containment = FullyContainedHost();
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("development",
            SandboxWorkloads.DevelopmentModeHostToolchain,
            provider,
            containment);

        AssertEx.False(summary.FilesystemIsolation);
        AssertEx.Equal("process", summary.Backend);
        AssertEx.Equal("Confined", summary.Level);

        // Egress IS served here and stays Yes: ResolveAgentFacingNetworkPolicy requests SandboxNetworkPolicy.None
        // wherever the flag is advertised, which is what that column reports.
        AssertEx.True(summary.NetworkIsolation);

        // Ceilings ARE served now: both sandboxes DevelopmentWorkspaceProvider creates ask for the node's numbers
        // through SandboxResourceCeilings, and this host can impose them. The row therefore carries no reason at all,
        // which is the only state that means "actually bounded".
        AssertEx.True(summary.ResourceLimits);
        AssertEx.Null(summary.ResourceLimitsUnavailableReason);

        // Denial is served but not REQUIRED with the switch off — the distinction the panel spells out.
        AssertEx.False(summary.NetworkIsolationRequired);

        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, "not requested by this role");
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason,
            SandboxWorkloads.DevelopmentModeHostToolchain.Workload);
    }

    /// <summary>
    ///     The other side of the reason contract: a role that DOES declare the boundary, on a host that cannot serve
    ///     it, gets the measured probe sentence rather than "the role did not ask".
    /// </summary>
    [Test]
    public void ToIsolationSummary_ForRunPythonOnAHostWithoutABoundary_ReportsTheMeasuredProbeReason()
    {
        const string Reason = "bwrap is not installed";
        var containment = FullyContainedHost() with
        {
            FilesystemIsolation = null,
            FilesystemIsolationUnavailableReason = Reason
        };
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("run_python",
            SandboxWorkloads.RunPython,
            provider,
            containment);

        AssertEx.False(summary.FilesystemIsolation);
        AssertEx.Equal("Confined", summary.Level);
        AssertEx.Equal(Reason, summary.FilesystemIsolationUnavailableReason);
    }

    /// <summary>
    ///     A real host shape — systemd user scopes and empty network namespaces work, bwrap does not — on the role
    ///     that asks for no boundary either way. Both reasons apply and the ROLE's wins: an operator told "bwrap is
    ///     not installed" here would go install it and see this row unchanged.
    /// </summary>
    [Test]
    public void ToIsolationSummary_WhenOnlySomeMechanismsAreActive_ReportsConfined()
    {
        var containment = FullyContainedHost() with
        {
            FilesystemIsolation = null,
            FilesystemIsolationUnavailableReason = "bwrap is not installed"
        };
        using var provider = CreateProcessProvider(containment);

        var summary = DevelopmentContractMapper.ToIsolationSummary("agent-home",
            SandboxWorkloads.AgentHome,
            provider,
            containment);

        AssertEx.Equal("Confined", summary.Level);
        AssertEx.Equal("process", summary.Backend);
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, "not requested by this role");
        AssertEx.False(summary.FilesystemIsolationUnavailableReason?.Contains("bwrap", StringComparison.Ordinal) == true);
    }

    [Test]
    public async Task ToIsolationSummary_ForTheContainerProviderOnTheDevelopmentRole_ReportsTheRolesServedPosture()
    {
        // A hardened container DOES have a filesystem boundary — read-only rootfs, engine-generated mounts only, no
        // host namespaces, every setting read back and fail-closed on mismatch — and advertises
        // SupportsHostFilesystemBoundary to say so. Having it and being asked for it are different questions, and this
        // projection answers the second.
        await using var provider = CreateDockerProvider();

        var summary = DevelopmentContractMapper.ToIsolationSummary("development",
            SandboxWorkloads.DevelopmentModeImageToolchain,
            provider,
            FullyContainedHost());

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, summary.Provider);
        AssertEx.Equal("docker", summary.Backend);
        AssertEx.True(summary.NetworkIsolation);
        AssertEx.True(summary.ReadOnlyMounts);

        // The container can impose ceilings and Development Mode asks for them, so this column is Yes here — and it
        // is the intersection that decides, not the backend's strength alone: hand the same declaration a backend
        // without the flag and it reads No with the host's reason.
        AssertEx.True(summary.ResourceLimits);
        AssertEx.Null(summary.ResourceLimitsUnavailableReason);

        // The container HAS the property, and the Development declaration still does not ask for it — so the served
        // posture is Confined with the not-requested reason, not Isolated. The role is what changed, not the backend:
        // hand this same provider a declaration that asks (run_python's) and it would report the boundary.
        AssertEx.False(summary.FilesystemIsolation);
        AssertEx.Equal("Confined", summary.Level);
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, "not requested by this role");
        AssertEx.True(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsHostFilesystemBoundary));

        // The narrower mechanism flag is still absent, and must stay absent: the provider refuses
        // SandboxIsolationMode.Filesystem on a create request, and advertising it would make that refusal a surprise.
        AssertEx.False(provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation));
    }

    [Test]
    public void ToIsolationSummary_ForTheDeterministicFake_ReportsNoneWhateverTheHostCanDo()
    {
        var summary = DevelopmentContractMapper.ToIsolationSummary("work-session",
            SandboxWorkloads.WorkSession,
            new FakeSandboxRuntimeProvider(TimeProvider.System),
            FullyContainedHost());

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name, summary.Provider);
        AssertEx.Equal("none", summary.Backend);
        AssertEx.Equal("None", summary.Level);
        AssertEx.Contains(summary.FilesystemIsolationUnavailableReason, "not requested by this role");
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
