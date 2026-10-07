namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Windows branch of <see cref="HostSandboxContainmentProbe" />, driven on any host through a substituted MXC runtime: the boundary
///     is reported only when MXC accepts the policy with no missing host prep, every other outcome is a reason, and nothing throws.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class HostSandboxContainmentProbeWindowsBranchTests
{
    private const string NullDeviceWarning =
        "AppContainer + DACL tier selected: AppContainer processes may be unable to open the NUL device. Run `wxc-host-prep prepare-null-device` (elevated).";

    [Test]
    public async Task Supported_WithoutHostPrepWarnings_ReportsAPreviewBoundary_AndHonestWindowsReasons()
    {
        var containment = Measure(Runtime(Probe(IsolationTier.AppContainerDacl)));

        AssertEx.True(containment.SupportsAppContainerBoundary);
        var boundary = AssertEx.NotNull(containment.AppContainerBoundary);
        AssertEx.Equal("mxc-processcontainer", boundary.Mechanism);
        AssertEx.Equal("AppContainerDacl", boundary.Tier);
        AssertEx.Equal(SandboxMechanismMaturity.Preview, boundary.Maturity);
        AssertEx.Null(containment.AppContainerBoundaryUnavailableReason);
        // No bwrap, no ceilings, no separate egress mechanism on Windows — each with a reason, none advertised.
        AssertEx.False(containment.SupportsFilesystemIsolation);
        AssertEx.False(containment.SupportsResourceLimits);
        AssertEx.False(containment.SupportsNetworkIsolation);
        AssertEx.Contains(containment.ResourceLimitsUnavailableReason, "bounded only by its timeout");
        AssertEx.Contains(containment.NetworkIsolationUnavailableReason, "AppContainer boundary");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MissingHostPrep_IsUnavailable_AndNamesTheExactCommand()
    {
        var containment = Measure(Runtime(Probe(IsolationTier.AppContainerDacl, [NullDeviceWarning])));

        AssertEx.False(containment.SupportsAppContainerBoundary);
        AssertEx.Contains(containment.AppContainerBoundaryUnavailableReason, "'wxc-host-prep prepare-null-device' (after every boot)");
        AssertEx.Contains(containment.AppContainerBoundaryUnavailableReason, NullDeviceWarning);
        AssertEx.Contains(containment.FilesystemIsolationUnavailableReason, "MXC AppContainer boundary");
        await Task.CompletedTask;
    }

    [Test]
    public async Task UnsupportedPlatform_IsUnavailable_WithMxcsReason()
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Returns(new PlatformSupport { IsSupported = false, Reason = "requires 26100" });

        var containment = Measure(runtime);

        AssertEx.False(containment.SupportsAppContainerBoundary);
        AssertEx.Contains(containment.AppContainerBoundaryUnavailableReason, "requires 26100");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ANativeFailure_NeverThrows()
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Throws(new DllNotFoundException("mxc_ffi.dll"));

        var containment = Measure(runtime);

        AssertEx.False(containment.SupportsAppContainerBoundary);
        AssertEx.Contains(containment.AppContainerBoundaryUnavailableReason, "mxc_ffi.dll");
        await Task.CompletedTask;
    }

    [Test]
    public async Task NoRuntime_IsUnavailable()
    {
        var containment = Measure(mxcRuntime: null);

        AssertEx.False(containment.SupportsAppContainerBoundary);
        AssertEx.Contains(containment.AppContainerBoundaryUnavailableReason, "MXC runtime is not available");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DescribeMissingHostPrep_NamesBothCommands_WhenBothAreMissing()
    {
        var reason = HostSandboxContainmentProbe.DescribeMissingHostPrep(["run wxc-host-prep prepare-system-drive", NullDeviceWarning]);

        AssertEx.Contains(reason, "'wxc-host-prep prepare-system-drive' (once per host) and 'wxc-host-prep prepare-null-device' (after every boot)");
        await Task.CompletedTask;
    }

    private static SandboxContainment Measure(IMxcSandboxRuntime? mxcRuntime)
    {
        // The filesystem probe must never run on the Windows branch: it would start a bwrap chain.
        var probe = new HostSandboxContainmentProbe(logger: null,
            _ => throw new InvalidOperationException("the bwrap probe must not run on the Windows branch"),
            mxcRuntime,
            isWindows: static () => true);
        return probe.Containment;
    }

    private static IMxcSandboxRuntime Runtime(ProbeOutput probe)
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Returns(new PlatformSupport { IsSupported = true, AvailableMethods = [ContainmentBackend.ProcessContainer] });
        runtime.Probe(Arg.Any<ContainerRequest?>()).Returns(probe);
        return runtime;
    }

    private static ProbeOutput Probe(IsolationTier tier, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Tier = tier,
            Warnings = warnings ?? [],
            Probes = new ProbeFacts { UiCapabilities = new UiCapabilitySupport() }
        };
}
