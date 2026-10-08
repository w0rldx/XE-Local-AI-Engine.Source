namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary><see cref="MxcProbe.Measure" /> reports what MXC says, and degrades to "unavailable with a reason" instead of throwing.</summary>
[Category(TestCategories.Unit)]
public sealed class MxcProbeTests
{
    private const string HostPrepWarning =
        "AppContainer + DACL tier selected: AppContainer processes may be unable to open the NUL device. Run `wxc-host-prep prepare-null-device` (elevated).";

    [Test]
    public async Task Measure_WhenMxcReportsATier_IsSupportedWithTierAndVerbatimWarnings()
    {
        var runtime = Runtime(Probe(IsolationTier.AppContainerDacl, warnings: [HostPrepWarning]));

        var result = MxcProbe.Measure(runtime);

        AssertEx.True(result.Supported);
        AssertEx.Equal("AppContainerDacl", result.Tier);
        AssertEx.Equal(HostPrepWarning, result.Warnings.Single());
        AssertEx.True(result.HostPrepMissing);
        AssertEx.Null(result.Reason);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Measure_ProbesTheExactPolicyEveryLaunchUses()
    {
        var runtime = Runtime(Probe(IsolationTier.AppContainerDacl));

        AssertEx.False(MxcProbe.Measure(runtime).HostPrepMissing);

        runtime.Received(1).Probe(Arg.Is<ContainerRequest>(request =>
            request.Containment is Containment.ProcessContainer
            && request.Network!.Egress!.Default == NetworkAction.Deny
            && request.Filesystem == null));
        await Task.CompletedTask;
    }

    [Test]
    public async Task Measure_WhenPlatformIsUnsupported_ReportsMxcsReasonWithoutProbing()
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Returns(new PlatformSupport
        {
            IsSupported = false,
            Reason = "requires Windows 11 24H2"
        });

        var result = MxcProbe.Measure(runtime);

        AssertEx.False(result.Supported);
        AssertEx.Contains(result.Reason, "requires Windows 11 24H2");
        runtime.DidNotReceive().Probe(Arg.Any<ContainerRequest?>());
        await Task.CompletedTask;
    }

    [Test]
    public async Task Measure_WhenProcessContainerIsNotAnAvailableMethod_IsUnsupported()
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Returns(new PlatformSupport
        {
            IsSupported = true,
            AvailableMethods = [ContainmentBackend.Bubblewrap]
        });

        var result = MxcProbe.Measure(runtime);

        AssertEx.False(result.Supported);
        AssertEx.Contains(result.Reason, "ProcessContainer");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Measure_WhenProbeReturnsAnError_IsUnsupportedWithThatErrorAndWarnings()
    {
        var runtime = Runtime(Probe(tier: null, error: "egress deny with ingress allow is rejected", warnings: ["w1"]));

        var result = MxcProbe.Measure(runtime);

        AssertEx.False(result.Supported);
        AssertEx.Null(result.Tier);
        AssertEx.Contains(result.Reason, "egress deny with ingress allow is rejected");
        AssertEx.Equal("w1", result.Warnings.Single());
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("mxc")]
    [Arguments("dll")]
    [Arguments("other")]
    public async Task Measure_WhenTheSdkThrows_NeverThrows(string kind)
    {
        Exception exception = kind switch
        {
            "mxc" => new MxcException(ErrorCode.BackendUnavailable, "backend unavailable"),
            "dll" => new DllNotFoundException("mxc_ffi.dll"),
            _ => new InvalidOperationException("boom"),
        };
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Throws(exception);

        var result = MxcProbe.Measure(runtime);

        AssertEx.False(result.Supported);
        AssertEx.NotNullOrEmpty(result.Reason);
        AssertEx.Empty(result.Warnings);
        await Task.CompletedTask;
    }

    private static IMxcSandboxRuntime Runtime(ProbeOutput probe)
    {
        var runtime = Substitute.For<IMxcSandboxRuntime>();
        runtime.GetPlatformSupport().Returns(new PlatformSupport
        {
            IsSupported = true,
            AvailableMethods = [ContainmentBackend.ProcessContainer]
        });
        runtime.Probe(Arg.Any<ContainerRequest?>()).Returns(probe);
        return runtime;
    }

    private static ProbeOutput Probe(IsolationTier? tier, string? error = null, IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Tier = tier,
            Error = error,
            Warnings = warnings ?? [],
            Probes = new ProbeFacts
            {
                UiCapabilities = new UiCapabilitySupport()
            },
        };
}
