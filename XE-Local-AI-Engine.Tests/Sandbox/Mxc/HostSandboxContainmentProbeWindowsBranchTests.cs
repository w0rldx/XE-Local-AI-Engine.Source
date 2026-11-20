namespace XE_Local_AI_Engine.Tests.Sandbox.Mxc;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Mxc.Sdk.V1;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;
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
        // No bwrap and no separate egress mechanism on Windows; ceilings only where the Job Object probe proved them (not here).
        AssertEx.False(containment.SupportsFilesystemIsolation);
        AssertEx.False(containment.SupportsResourceLimits);
        AssertEx.Null(containment.WindowsJobProcessorCount);
        AssertEx.False(containment.SupportsNetworkIsolation);
        AssertEx.Equal(JobsUnsupported.Reason!, containment.ResourceLimitsUnavailableReason);
        AssertEx.Contains(containment.NetworkIsolationUnavailableReason, "AppContainer boundary");
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheProbeLine_NamesMxcsSixUiFacts_AndTheOsVersion()
    {
        // The log line is the only record of the facts the boundary decision reads (no contract carries them).
        var logger = new RecordingLogger<HostSandboxContainmentProbe>();
        _ = Measure(Runtime(Probe(IsolationTier.AppContainerDacl, canBlockDesktopSwitching: false)), logger);

        AssertEx.Contains(logger.AllText,
            "MXC UI facts: CanBlockClipboardRead true, CanBlockClipboardWrite true, CanBlockInputInjection true, CanBlockExternalUiObjects true, CanBlockGlobalUiNamespace true, CanBlockDesktopSwitching false;");
        // A real Windows host appends " (UBR n)" before the semicolon; the UBR read is Windows-only, its format is pinned below.
        AssertEx.Contains(logger.AllText, $"OS {RuntimeInformation.OSDescription}");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DescribeOs_AppendsTheUpdateBuildRevision_TheMxcFloorIsDefinedBy()
    {
        AssertEx.Equal("Microsoft Windows 10.0.26200 (UBR 9457)", HostSandboxContainmentProbe.DescribeOs("Microsoft Windows 10.0.26200", 9457));
        await Task.CompletedTask;
    }

    [Test]
    public async Task DescribeOs_KeepsThePlainDescription_WhenTheRevisionIsUnknown()
    {
        AssertEx.Equal("Microsoft Windows 10.0.26200", HostSandboxContainmentProbe.DescribeOs("Microsoft Windows 10.0.26200", null));
        await Task.CompletedTask;
    }

    [Test]
    public async Task TheProbeLine_SaysNotReported_WhenMxcNeverReachedTheUiFacts()
    {
        var logger = new RecordingLogger<HostSandboxContainmentProbe>();

        _ = Measure(mxcRuntime: null, logger);

        AssertEx.Contains(logger.AllText, "CanBlockClipboardRead not reported,");
        AssertEx.Contains(logger.AllText, "CanBlockDesktopSwitching not reported;");
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
        runtime.GetPlatformSupport().Returns(new PlatformSupport
        {
            IsSupported = false,
            Reason = "requires 26100"
        });

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
    public async Task AProvenJobProbe_AdvertisesCeilings_WithItsProcessorCount_AndLeavesTheBoundaryAlone()
    {
        var containment = Measure(Runtime(Probe(IsolationTier.AppContainerDacl)),
            jobProbe: static () => new WindowsJobProbeResult
            {
                Supported = true,
                SystemProcessorCount = 16
            });

        AssertEx.True(containment.SupportsResourceLimits);
        AssertEx.Equal(16, containment.WindowsJobProcessorCount);
        AssertEx.Null(containment.ResourceLimitsUnavailableReason);
        AssertEx.Null(containment.SystemdRunPath, "a Windows host never has a systemd chain");
        AssertEx.True(containment.SupportsAppContainerBoundary);
        await Task.CompletedTask;
    }

    [Test]
    public async Task AThrowingJobProbe_WithdrawsOnlyTheCeilings_WithItsReason()
    {
        var containment = Measure(Runtime(Probe(IsolationTier.AppContainerDacl)),
            jobProbe: static () => throw new InvalidOperationException("kernel32 refused"));

        AssertEx.False(containment.SupportsResourceLimits);
        AssertEx.Null(containment.WindowsJobProcessorCount);
        AssertEx.Contains(containment.ResourceLimitsUnavailableReason, "kernel32 refused");
        AssertEx.True(containment.SupportsAppContainerBoundary, "a failed job probe must not erase the measured boundary");
        await Task.CompletedTask;
    }

    [Test]
    public async Task AStalledJobProbe_IsCutOffAtItsDeadline_KeepsTheBoundary_AndItsLateRunLeavesNoHandle()
    {
        // The real probe over the fake native seam, with its first process create stalled until the test lets it go.
        var api = new FakeWindowsJobApi();
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        api.BeforeStep = step =>
        {
            if (step == "CreateSuspended" && stalled.TrySetResult())
            {
                release.Wait();
            }
        };
        var time = new ManualTimeProvider();
        var logger = new RecordingLogger<HostSandboxContainmentProbe>();
        var probe = new HostSandboxContainmentProbe(logger,
            _ => throw new InvalidOperationException("the bwrap probe must not run on the Windows branch"),
            Runtime(Probe(IsolationTier.AppContainerDacl)),
            isWindows: static () => true,
            () => WindowsJobCeilingsProbe.Measure(new WindowsJobLauncher(api), @"C:\Windows\System32\cmd.exe"),
            time);
        try
        {
            var measuring = Task.Run(() => probe.Containment);
            await AssertEx.CompletesAsync(stalled.Task, TestBudgets.Contended, "the probe must reach its first process create");
            await AssertEx.EventuallyAsync(() => time.ArmedTimerCount > 0, TestBudgets.Contended, "the probe's deadline must be armed");
            await AssertEx.StaysIncompleteAsync(measuring, "the containment result waits for the job probe until its deadline");
            time.Advance(WindowsJobCeilingsProbe.Deadline);

            await AssertEx.CompletesAsync(measuring, TestBudgets.Contended, "the containment result must return at the deadline");
            var containment = await measuring;
            AssertEx.False(containment.SupportsResourceLimits);
            AssertEx.Null(containment.WindowsJobProcessorCount);
            AssertEx.Contains(containment.ResourceLimitsUnavailableReason, "the Windows Job Object ceilings probe did not finish within 90 s");
            AssertEx.True(containment.SupportsAppContainerBoundary, "a stalled job probe must not erase the measured boundary");
            AssertEx.Equal("AppContainerDacl", AssertEx.NotNull(containment.AppContainerBoundary).Tier);
        }
        finally
        {
            release.Set();
        }

        // The late run finishes its controls and closes every job, process and pipe handle it opened.
        await AssertEx.EventuallyAsync(() => logger.AllText.Contains("finished after its 90 s deadline", StringComparison.Ordinal),
            TestBudgets.Contended,
            "the late probe must finish once its native step returns");
        AssertEx.True(api.IssuedHandles > 0, "the late run must have opened handles for this check to mean anything");
        AssertEx.Equal(0, api.OpenHandles, "the late probe leaked a handle");
    }

    [Test]
    public async Task DescribeMissingHostPrep_NamesBothCommands_WhenBothAreMissing()
    {
        var reason = HostSandboxContainmentProbe.DescribeMissingHostPrep(["run wxc-host-prep prepare-system-drive", NullDeviceWarning]);

        AssertEx.Contains(reason, "'wxc-host-prep prepare-system-drive' (once per host) and 'wxc-host-prep prepare-null-device' (after every boot)");
        await Task.CompletedTask;
    }

    private static SandboxContainment Measure(IMxcSandboxRuntime? mxcRuntime,
        ILogger<HostSandboxContainmentProbe>? logger = null,
        Func<WindowsJobProbeResult>? jobProbe = null)
    {
        // The filesystem probe must never run on the Windows branch: it would start a bwrap chain.
        var probe = new HostSandboxContainmentProbe(logger,
            _ => throw new InvalidOperationException("the bwrap probe must not run on the Windows branch"),
            mxcRuntime,
            isWindows: static () => true,
            jobProbe ?? (static () => JobsUnsupported),
            TimeProvider.System);
        return probe.Containment;
    }

    private static readonly WindowsJobProbeResult JobsUnsupported = new()
    {
        Supported = false,
        Reason = "the Windows Job Object ceilings probe failed: a 512 MB commit inside a 256 MB job succeeded"
    };

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

    private static ProbeOutput Probe(IsolationTier tier, IReadOnlyList<string>? warnings = null, bool canBlockDesktopSwitching = true) =>
        new()
        {
            Tier = tier,
            Warnings = warnings ?? [],
            Probes = new ProbeFacts
            {
                UiCapabilities = new UiCapabilitySupport
                {
                    CanBlockClipboardRead = true,
                    CanBlockClipboardWrite = true,
                    CanBlockInputInjection = true,
                    CanBlockExternalUiObjects = true,
                    CanBlockGlobalUiNamespace = true,
                    CanBlockDesktopSwitching = canBlockDesktopSwitching,
                }
            }
        };
}
