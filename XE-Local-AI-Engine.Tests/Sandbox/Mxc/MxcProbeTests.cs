namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Reflection;
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
    public async Task Measure_WhenMxcCanBlockEveryUiChannel_IsSupportedWithNoUiWarning()
    {
        var result = MxcProbe.Measure(Runtime(Probe(IsolationTier.AppContainerDacl)));

        AssertEx.True(result.Supported);
        AssertEx.Empty(result.Warnings);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("CanBlockClipboardRead")]
    [Arguments("CanBlockClipboardWrite")]
    [Arguments("CanBlockInputInjection")]
    public async Task Measure_WhenMxcReportsItCannotBlockClipboardOrInputInjection_RefusesTheBoundary(string missing)
    {
        // Win32k is allowed, so these denials are the UI boundary: a host that reports it cannot enforce them must not serve the floor.
        var probe = Probe(IsolationTier.AppContainerDacl, ui: UiBlockingExcept(missing));

        var result = MxcProbe.Measure(Runtime(probe));

        AssertEx.False(result.Supported);
        AssertEx.Equal(MxcProbe.UiBoundaryUnenforceableReason, result.Reason);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("ProbesMissing")]
    [Arguments("UiCapabilitiesMissing")]
    public async Task Measure_WhenMxcDoesNotReportItsUiFacts_StaysSupportedAndWarns(string absent)
    {
        // A missing field on a real host is no evidence of weakness: a Preview mechanism warns rather than switching itself off.
        var probe = new ProbeOutput
        {
            Tier = IsolationTier.AppContainerDacl,
            Warnings = [],
            Probes = absent == "ProbesMissing"
                ? null!
                : new ProbeFacts
                {
                    UiCapabilities = null!
                },
        };

        var result = MxcProbe.Measure(Runtime(probe));

        AssertEx.True(result.Supported);
        AssertEx.Equal(MxcProbe.UiFactsNotReportedWarning, result.Warnings.Single());
        AssertEx.False(result.HostPrepMissing);
        AssertEx.Null(result.UiFacts);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("CanBlockExternalUiObjects")]
    [Arguments("CanBlockGlobalUiNamespace")]
    [Arguments("CanBlockDesktopSwitching")]
    public async Task Measure_WhenMxcCannotEnforceAUiIsolationFlag_StaysSupportedAndWarnsNamingIt(string flag)
    {
        var result = MxcProbe.Measure(Runtime(Probe(IsolationTier.AppContainerDacl, ui: UiBlockingExcept(flag))));

        AssertEx.True(result.Supported);
        AssertEx.Contains(result.Warnings.Single(), flag);
        AssertEx.False(result.HostPrepMissing);
        // N-46: the facts ride to the containment log line as MXC reported them.
        var facts = AssertEx.NotNull(result.UiFacts);
        AssertEx.True(facts.CanBlockClipboardRead && facts.CanBlockClipboardWrite && facts.CanBlockInputInjection);
        AssertEx.Equal(flag != nameof(UiCapabilitySupport.CanBlockDesktopSwitching), facts.CanBlockDesktopSwitching);
        await Task.CompletedTask;
    }

    [Test]
    public async Task NoShippedText_NamesWin32k()
    {
        // N-45: the reason and warnings reach the isolation panel, which speaks of "the Windows UI kernel interface", never "Win32k".
        string[] flags =
        [
            "", nameof(UiCapabilitySupport.CanBlockClipboardRead), nameof(UiCapabilitySupport.CanBlockClipboardWrite),
            nameof(UiCapabilitySupport.CanBlockInputInjection), nameof(UiCapabilitySupport.CanBlockExternalUiObjects),
            nameof(UiCapabilitySupport.CanBlockGlobalUiNamespace), nameof(UiCapabilitySupport.CanBlockDesktopSwitching)
        ];
        var produced = flags.Select(flag => MxcProbe.Measure(Runtime(Probe(IsolationTier.AppContainerDacl, ui: UiBlockingExcept(flag)))))
                            .Append(MxcProbe.Measure(Runtime(new ProbeOutput
                            {
                                Tier = IsolationTier.AppContainerDacl,
                                Warnings = [],
                                Probes = null!
                            })))
                            .SelectMany(static result => result.Warnings.Append(result.Reason ?? string.Empty));
        var constants = typeof(MxcProbe).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                        .Where(static field => field.FieldType == typeof(string))
                                        .Select(static field => (string)field.GetValue(null)!)
                                        .ToList();

        AssertEx.NotEmpty(constants);
        AssertEx.Empty(constants.Concat(produced).Where(static text => text.Contains("Win32k", StringComparison.OrdinalIgnoreCase)));
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

    private static ProbeOutput Probe(IsolationTier? tier, string? error = null, IReadOnlyList<string>? warnings = null, UiCapabilitySupport? ui = null) =>
        new()
        {
            Tier = tier,
            Error = error,
            Warnings = warnings ?? [],
            Probes = new ProbeFacts
            {
                UiCapabilities = ui ?? UiBlockingExcept(notBlocked: "")
            },
        };

    /// <summary>MXC's UI facts with every channel blockable except the one named (by property name).</summary>
    private static UiCapabilitySupport UiBlockingExcept(string notBlocked) =>
        new()
        {
            CanBlockClipboardRead = notBlocked != nameof(UiCapabilitySupport.CanBlockClipboardRead),
            CanBlockClipboardWrite = notBlocked != nameof(UiCapabilitySupport.CanBlockClipboardWrite),
            CanBlockInputInjection = notBlocked != nameof(UiCapabilitySupport.CanBlockInputInjection),
            CanBlockInputMethodChanges = true,
            CanBlockExternalUiObjects = notBlocked != nameof(UiCapabilitySupport.CanBlockExternalUiObjects),
            CanBlockGlobalUiNamespace = notBlocked != nameof(UiCapabilitySupport.CanBlockGlobalUiNamespace),
            CanBlockDesktopSwitching = notBlocked != nameof(UiCapabilitySupport.CanBlockDesktopSwitching),
            CanBlockLogoffOrShutdown = true,
            CanBlockSystemParameterChanges = true,
            CanBlockDisplaySettingsChanges = true,
        };
}
