namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The provider's Windows Job Object branch (ADR 0025), on any host, through <see cref="WindowsJobLauncher" /> over a fake native seam.</summary>
/// <remarks>
///     A containment that maps job ceilings sends every command into its job, never a bare <c>Process</c>; a launch-time setup failure
///     refuses the command with nothing running, no handle open and no marker left; and the job dies with the command.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ProcessSandboxWindowsJobTests
{
    private static readonly SandboxContainment WindowsJobs = SandboxContainment.None with
    {
        SupportsResourceLimits = true,
        WindowsJobProcessorCount = 16
    };

    private static readonly SandboxResourceLimits Limits = new()
    {
        CpuCount = 2,
        MemoryMb = 4096,
        PidsLimit = 128
    };

    [Test]
    public async Task ACommandWithCeilings_RunsInItsJob_WithTheMappedLimits_AndTheJobDiesWithIt()
    {
        var api = new FakeWindowsJobApi { StandardOutputText = "hello\n" };
        using var provider = Provider(api);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("run", "tool.exe", "a b"));

        AssertEx.True(result.Completed, result.StandardError);
        AssertEx.Equal(0, result.ExitCode);
        AssertEx.Equal("hello\n", result.StandardOutput);
        var started = AssertEx.NotNull(api.Started.SingleOrDefault());
        AssertEx.Equal(WindowsCommandLine.Join("tool.exe", ["a b"]), started.CommandLine);
        AssertEx.True(started.Environment["HOME"].StartsWith(AssertEx.NotNull(handle.WorkingRoot), StringComparison.Ordinal),
            "the job child gets the provider's scrubbed environment, its HOME in the jail");
        AssertEx.Equal(WindowsJobLimits.Map(Limits.MemoryMb, Limits.PidsLimit, Limits.CpuCount, 16), api.LastSetLimits);
        AssertEx.True(api.Terminations >= 1, "the command's job is terminated when the command ends");
        AssertEx.Equal(0, api.OpenHandles, "every handle of the command's job is closed when the command ends");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task ANewlineFreeStdout_LargerThanTheCaptureBudget_IsBoundedAndReportedTruncated()
    {
        var api = new FakeWindowsJobApi { StandardOutputText = new string('x', MxcChildProcess.MaxPendingLineChars + 4096) };
        using var provider = Provider(api);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("no-newline", "tool.exe"));

        AssertEx.True(result.Completed, result.StandardError);
        AssertEx.True(result.StandardOutputTruncated, "output cut at the capture budget is reported truncated");
        AssertEx.True(result.StandardOutput.Length <= MxcChildProcess.MaxPendingLineChars, $"captured {result.StandardOutput.Length} chars, past the budget");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task ACutLine_ReachesTheCapture_ButTheLiveObserverGetsItWithheld()
    {
        // A cut value no longer matches its redaction, so the live view must not show the fragment that the capture keeps.
        var api = new FakeWindowsJobApi { StandardOutputText = new string('x', MxcChildProcess.MaxPendingLineChars + 16) + "\nnext\n" };
        var observer = new RecordingSandboxOutputObserver();
        using var provider = Provider(api);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("cut-observed", "tool.exe") with { OutputObserver = observer });

        AssertEx.True(result.Completed, result.StandardError);
        AssertEx.Equal($"{SandboxStderrTail.WithheldLine}|next", string.Join('|', observer.StandardOutput), "the cut line is withheld from the live view, the complete one is not");
        AssertEx.True(result.StandardOutput.StartsWith(new string('x', 64), StringComparison.Ordinal), "the capture keeps the cut line's text");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task StandardInput_ReachesTheJobChild_AsUtf8()
    {
        var api = new FakeWindowsJobApi();
        using var provider = Provider(api);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("stdin", "tool.exe") with { StandardInput = "grüße" });

        AssertEx.Equal(0, result.ExitCode);
        using var reader = new StreamReader(AssertEx.NotNull(api.LastStandardInput).Reader, System.Text.Encoding.UTF8);
        AssertEx.Equal("grüße", await reader.ReadToEndAsync());
        await provider.KillAsync(handle);
    }

    [Test]
    [Arguments("CreateJob")]
    [Arguments("SetExtendedLimits")]
    [Arguments("QueryLimits")]
    [Arguments("CreateSuspended")]
    [Arguments("SetAffinityMask")]
    [Arguments("SetPriorityClass")]
    [Arguments("Resume")]
    public async Task ALaunchTimeJobFailure_RefusesTheCommand_AndLeavesNothing(string step)
    {
        // Probe-versus-launch drift: the probe advertised the ceilings, then the launch-time job setup fails. Never a plain launch.
        var api = new FakeWindowsJobApi { FailAt = step };
        var markers = new CountingMarkerStore();
        using var provider = Provider(api, markers);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("drift", "tool.exe"));

        AssertRefused(result, api, markers);
        AssertEx.Equal(string.Equals(step, "Resume", StringComparison.Ordinal), api.Calls.Contains("Resume"));
        await provider.KillAsync(handle);
    }

    /// <summary>A launch failure after a passed probe refuses the command: one job launch, no plain fallback, nothing run.</summary>
    [Test]
    public async Task APostProbeLaunchFailure_IsARefusal_WithNoSecondLaunch()
    {
        var api = new FakeWindowsJobApi { FailAt = "CreateSuspended" };
        using var provider = Provider(api);
        var handle = await provider.CreateOrAttachAsync(Request());
        var marker = Path.Combine(Path.GetTempPath(), "xe-job-refusal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await provider.ExecuteAsync(handle, WritesMarker("post-probe", marker));

            AssertEx.True(result.LaunchFailed, "a failed job launch refuses the command");
            AssertEx.Contains(result.StandardError, "inside its Windows job");
            AssertEx.Equal(1, api.Calls.Count(static call => call == "CreateSuspended"), "the command is launched once, never retried");
            AssertEx.False(File.Exists(marker), "the command ran outside its job");
        }
        finally
        {
            File.Delete(marker);
            await provider.KillAsync(handle);
        }
    }

    [Test]
    public async Task AChildOutsideItsJob_IsNeverResumed_AndTheCommandIsRefused()
    {
        var api = new FakeWindowsJobApi { Member = false };
        var markers = new CountingMarkerStore();
        using var provider = Provider(api, markers);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("not-a-member", "tool.exe"));

        AssertRefused(result, api, markers);
        AssertEx.False(api.Calls.Contains("Resume"));
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task AnInteractiveServer_RunsInItsJob_AndItsDisposeTerminatesTheJob()
    {
        var api = new FakeWindowsJobApi { ExitCodeFor = static _ => null };
        using var provider = Provider(api);
        var handle = await provider.CreateOrAttachAsync(Request());

        var server = await provider.StartInteractiveAsync(handle, Command("server", "server.exe"));
        AssertEx.Null(server.ExitCode);
        await server.DisposeAsync();

        AssertEx.True(api.Terminations >= 1);
        AssertEx.Equal(0, api.OpenHandles);
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task AFailedStdinClose_RefusesTheCommand_BeforeItIsResumed_AndDisposesItsJob()
    {
        var api = new FakeWindowsJobApi { StandardInputFailsOnDispose = true, ExitCodeFor = static _ => null };
        var markers = new CountingMarkerStore();
        using var provider = Provider(api, markers);
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("stdin-close", "tool.exe"));

        AssertRefused(result, api, markers);
        AssertEx.False(api.Calls.Contains("Resume"), "unused stdin is closed while the child is still suspended, so a refusal means nothing ran");
        AssertEx.True(api.Terminations >= 1, "the suspended command's job is terminated, not left for a finalizer");
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task CeilingsNoMechanismApplies_RefuseTheCommand_RatherThanThrowing()
    {
        // The probe advertised ceilings but measured nothing to apply them with: SandboxLaunchPlan.Create refuses (ADR 0025), and the
        // command path turns that into a failed launch with nothing run.
        var api = new FakeWindowsJobApi();
        using var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new FixedProbe(SandboxContainment.None with { SupportsResourceLimits = true })),
            new CountingMarkerStore(),
            windowsJobLauncher: new WindowsJobLauncher(api));
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("no-mechanism", "tool.exe"));

        AssertEx.True(result.LaunchFailed);
        AssertEx.Contains(result.StandardError, "neither a systemd scope nor a Windows job");
        AssertEx.Empty(api.Started);
        await provider.KillAsync(handle);
    }

    [Test]
    public async Task AJobLaunch_WithoutALauncherInThisProcess_IsRefused()
    {
        Skip.When(OperatingSystem.IsWindows(), "a Windows host always constructs the real launcher");
        using var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new FixedProbe(WindowsJobs)),
            new CountingMarkerStore());
        var handle = await provider.CreateOrAttachAsync(Request());

        var result = await provider.ExecuteAsync(handle, Command("no-launcher", "tool.exe"));

        AssertEx.True(result.LaunchFailed);
        AssertEx.Contains(result.StandardError, "Windows Job Object launcher is not available");
        await provider.KillAsync(handle);
    }

    private static void AssertRefused(SandboxCommandResult result, FakeWindowsJobApi api, CountingMarkerStore markers)
    {
        AssertEx.True(result.LaunchFailed, "a job setup failure refuses the command");
        AssertEx.False(result.Completed);
        AssertEx.Contains(result.StandardError, "inside its Windows job");
        AssertEx.Equal(0, api.OpenHandles, "no handle survives a refused launch");
        AssertEx.Equal(0, markers.Live, "no process marker survives a refused launch");
    }

    private static ProcessSandboxRuntimeProvider Provider(FakeWindowsJobApi api, ISandboxMarkerStore? markers = null) =>
        new(Options.Create(new LocalContainerOptions()),
            TimeProvider.System,
            logger: null,
            new SandboxLauncher(new FixedProbe(WindowsJobs)),
            markers ?? new CountingMarkerStore(),
            windowsJobLauncher: new WindowsJobLauncher(api));

    internal static SandboxCreateRequest Request() =>
        new()
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = ProcessSandboxRuntimeProvider.Name,
                RuntimeProfile = "windows-job-" + Guid.NewGuid().ToString("N"),
                ManifestVersion = 1
            },
            RuntimeProfile = "agent-home",
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            ResourceLimits = Limits
        };

    private static SandboxCommandRequest Command(string id, string executable, params string[] arguments) =>
        new()
        {
            ExecutionId = "windows-job-" + id,
            Executable = executable,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        };

    internal static SandboxCommandRequest WritesMarker(string id, string marker) =>
        OperatingSystem.IsWindows()
            ? Command(id, "cmd.exe", "/d", "/c", $"echo x>{marker}")
            : Command(id, "sh", "-c", $"echo x > '{marker}'");

    internal sealed class FixedProbe : ISandboxContainmentProbe
    {
        public FixedProbe(SandboxContainment containment)
        {
            Containment = containment;
        }

        public SandboxContainment Containment { get; }
    }

    internal sealed class CountingMarkerStore : ISandboxMarkerStore
    {
        private readonly Dictionary<string, SandboxProcessMarker> _live = [];

        public int Live => _live.Count;

        public string? Write(SandboxProcessMarker marker)
        {
            var id = Guid.NewGuid().ToString("N");
            _live[id] = marker;
            return id;
        }

        public void Update(string markerId, SandboxProcessMarker marker) => _live[markerId] = marker;

        public void Delete(string markerId) => _live.Remove(markerId);

        public IReadOnlyList<SandboxMarkerEntry> ReadAll() => [.. _live.Select(static entry => new SandboxMarkerEntry(entry.Key, entry.Value))];
    }
}
