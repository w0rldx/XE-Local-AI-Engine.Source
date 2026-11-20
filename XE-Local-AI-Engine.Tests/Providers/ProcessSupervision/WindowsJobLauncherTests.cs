namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Architecture.Support;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The Job Object mapping and the launcher's ordering and cleanup (ADR 0025), through a fake <see cref="IWindowsJobApi" />.</summary>
/// <remarks>The real kernel32 calls are proven by <c>WindowsJobSandboxWindowsTests</c>.</remarks>
[Category(TestCategories.Unit)]
public sealed class WindowsJobLauncherTests
{
    private static readonly WindowsJobStartInfo Command = new()
    {
        CommandLine = "cmd.exe /d /c exit 0",
        Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    };

    [Test]
    [Arguments(4096, 4_294_967_296UL)]
    [Arguments(2048, 2_147_483_648UL)]
    [Arguments(int.MaxValue, 2_251_799_812_636_672UL)]
    public async Task Map_ComputesTheCommitLimit_In64Bits(int memoryMb, ulong expectedBytes)
    {
        var limits = WindowsJobLimits.Map(memoryMb, pidsLimit: null, cpuCount: null, systemProcessorCount: 16);

        AssertEx.Equal(expectedBytes, limits.JobMemoryLimit);
        AssertEx.Equal(WindowsJobLimits.LimitJobMemory, limits.LimitFlags & WindowsJobLimits.LimitJobMemory);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments(1d, 16, 625u)]
    [Arguments(2d, 32, 625u)]
    [Arguments(0.0001d, 16, 1u)]
    [Arguments(64d, 16, 10_000u)]
    [Arguments(16d, 16, 10_000u)]
    [Arguments(1.5d, 4, 3750u)]
    public async Task Map_RatesCpuAgainstTheWholeSystem_RoundingUp_AndClamped(double cpuCount, int systemProcessors, uint expectedRate)
    {
        AssertEx.Equal(expectedRate, WindowsJobLimits.Map(memoryMb: null, pidsLimit: null, cpuCount, systemProcessors).CpuRate);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Map_AlwaysKillsOnClose_AndDiesOnUnhandledExceptions_AndNeverAllowsBreakaway()
    {
        var full = WindowsJobLimits.Map(1024, 64, 2, 8);
        var empty = WindowsJobLimits.Map(null, null, null, 8);
        var nonPositive = WindowsJobLimits.Map(0, -1, 0, 8);

        AssertEx.Equal(WindowsJobLimits.LimitKillOnJobClose | WindowsJobLimits.LimitDieOnUnhandledException | WindowsJobLimits.LimitJobMemory
                       | WindowsJobLimits.LimitActiveProcess,
            full.LimitFlags);
        AssertEx.Equal(64u, full.ActiveProcessLimit);
        foreach (var limits in new[] { empty, nonPositive })
        {
            AssertEx.Equal(WindowsJobLimits.LimitKillOnJobClose | WindowsJobLimits.LimitDieOnUnhandledException, limits.LimitFlags);
            AssertEx.Equal(0UL, limits.JobMemoryLimit);
            AssertEx.Equal(0u, limits.ActiveProcessLimit);
            AssertEx.Equal(0u, limits.CpuRate);
        }

        foreach (var limits in new[] { full, empty, nonPositive })
        {
            AssertEx.Equal(0u, limits.LimitFlags & (WindowsJobLimits.LimitBreakawayOk | WindowsJobLimits.LimitSilentBreakawayOk));
        }

        _ = AssertEx.Throws<ArgumentOutOfRangeException>(() => WindowsJobLimits.Map(1024, null, 1, systemProcessorCount: 0));
        await Task.CompletedTask;
    }

    /// <summary>No engine-side job may carry UI limits (ADR 0025): the job interop has no UI-restriction class or setter at all.</summary>
    [Test]
    public async Task TheJobInterop_HasNoUiRestrictionCall()
    {
        var sources = Directory.GetFiles(Path.Combine(RepositoryPaths.Root, "XE-Local-AI-Engine.Providers.ProcessSupervision"), "*.cs", SearchOption.AllDirectories)
                               .Where(static path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("\\obj\\", StringComparison.Ordinal))
                               .ToList();
        AssertEx.True(sources.Exists(static path => path.EndsWith("WindowsJobNative.cs", StringComparison.Ordinal)), "the scan must see the job interop");

        foreach (var path in sources)
        {
            var code = SourceCommentStripper.StripCommentsAndLiterals(await File.ReadAllTextAsync(path));
            foreach (var banned in new[] { "UI_RESTRICTIONS", "UIRestrictions", "UiRestrictions", "JobObjectBasicUIRestrictions", "UILIMIT_" })
            {
                AssertEx.False(code.Contains(banned, StringComparison.OrdinalIgnoreCase), $"{path} names {banned}: a job must never carry UI limits.");
            }
        }
    }

    [Test]
    public async Task Start_LimitsAndReadsBack_ThenCreatesSuspended_VerifiesMembership_ThenResumes()
    {
        var api = new FakeWindowsJobApi();
        var limits = WindowsJobLimits.Map(2048, 64, 2, 16);

        using var process = new WindowsJobLauncher(api).Start(Command, limits);

        AssertEx.Equal("CreateJob,SetExtendedLimits,SetCpuRate,QueryLimits,CreateSuspended,IsProcessInJob,GetEngineScheduling,SetScheduling,WaitForExitAsync,Resume",
            string.Join(',', api.Calls));
        AssertEx.Equal(Command, api.Started[0]);
        await process.WaitForExitAsync(CancellationToken.None);
        AssertEx.Equal(0, process.ExitCode);
    }

    /// <summary>Each launch reads the engine's affinity and priority class anew and sets exactly those on its suspended command.</summary>
    [Test]
    public async Task EachLaunch_SetsTheEnginesCurrentAffinityAndPriority_BeforeTheResume()
    {
        var api = new FakeWindowsJobApi { EngineScheduling = new WindowsJobScheduling { AffinityMask = 0x3, PriorityClass = 0x4000 } };
        var launcher = new WindowsJobLauncher(api);

        using (launcher.Start(Command, WindowsJobLimits.Map(2048, 64, null, 16)))
        {
            AssertEx.True(api.Calls.IndexOf("SetScheduling") < api.Calls.IndexOf("Resume"), "the scheduling is set while the command is suspended");
        }

        api.EngineScheduling = new WindowsJobScheduling { AffinityMask = 0x5, PriorityClass = 0x8000 };
        using (launcher.Start(Command, WindowsJobLimits.Map(2048, 64, null, 16)))
        {
            AssertEx.Equal(2, api.Calls.Count(static call => call == "GetEngineScheduling"), "the engine's values are read at every launch");
        }

        AssertEx.Equal(new WindowsJobScheduling { AffinityMask = 0x3, PriorityClass = 0x4000 }, api.SetSchedulings[0]);
        AssertEx.Equal(new WindowsJobScheduling { AffinityMask = 0x5, PriorityClass = 0x8000 }, api.SetSchedulings[1]);
        AssertEx.Equal(2, api.SetSchedulings.Count);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Start_WithoutACpuCeiling_SetsNoRate()
    {
        var api = new FakeWindowsJobApi();

        using var process = new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, null, 16));

        AssertEx.False(api.Calls.Contains("SetCpuRate"));
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("CreateJob")]
    [Arguments("SetExtendedLimits")]
    [Arguments("SetCpuRate")]
    [Arguments("QueryLimits")]
    [Arguments("CreateSuspended")]
    [Arguments("IsProcessInJob")]
    [Arguments("GetEngineScheduling")]
    [Arguments("SetScheduling")]
    [Arguments("WaitForExitAsync")]
    [Arguments("Resume")]
    public async Task ASetupFailure_RefusesTheLaunch_NeverResumes_AndClosesEveryHandle(string step)
    {
        var api = new FakeWindowsJobApi { FailAt = step };

        var failure = AssertEx.Throws<System.ComponentModel.Win32Exception>(() => new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16)));

        AssertEx.Contains(failure.Message, step);
        AssertNothingSurvived(api, resumeAttempted: string.Equals(step, "Resume", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    [Test]
    public async Task AProcessThatIsNotAMember_IsNeverResumed()
    {
        var api = new FakeWindowsJobApi { Member = false };

        var failure = AssertEx.Throws<InvalidOperationException>(() => new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16)));

        AssertEx.Contains(failure.Message, "not a member");
        // The job's terminate cannot reach a process the job does not hold, so the launcher kills it directly.
        AssertEx.Equal(1, api.ProcessTerminations, "a non-member is terminated at process level");
        AssertNothingSurvived(api, resumeAttempted: false);
        await Task.CompletedTask;
    }

    /// <summary>The membership kill hands the held process handle over without duplicating it; an unconfirmed exit joins the failure.</summary>
    [Test]
    public void AFailedMembershipCheck_TransfersTheProcessHandle_AndReportsAnUnconfirmedExit()
    {
        var api = new FakeWindowsJobApi { Member = false, UnconfirmedExit = "; the command (pid 4242) was ended but its exit not confirmed" };

        var failure = AssertEx.Throws<InvalidOperationException>(() => new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16)));

        AssertEx.Contains(failure.Message, "not a member");
        AssertEx.Contains(failure.Message, "exit not confirmed");
        AssertEx.True(failure.InnerException is InvalidOperationException, "the original failure is kept as the cause");
        var held = AssertEx.NotNull(api.HeldProcesses.SingleOrDefault());
        AssertEx.False(held.IsClosed, "the child's Dispose never closes a process handle it handed over");
        AssertEx.Equal(1, api.OpenHandles, "everything but the held process handle is closed");
    }

    [Test]
    public async Task ATerminateFailureDuringCleanup_IsSwallowed_AndTheOriginalFailureIsReported()
    {
        var api = new FakeWindowsJobApi { Member = false, FailAt = "Terminate" };

        var failure = AssertEx.Throws<InvalidOperationException>(() => new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16)));

        AssertEx.Contains(failure.Message, "not a member");
        AssertEx.True(api.Calls.Contains("Terminate"), "the cleanup still tried the job's terminate");
        AssertEx.Equal(0, api.OpenHandles, "a failed terminate still closes every handle");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("breakaway")]
    [Arguments("silent-breakaway")]
    [Arguments("memory")]
    [Arguments("processes")]
    [Arguments("cpu")]
    [Arguments("kill-on-close")]
    public async Task AReadBackThatDiffers_InAnyField_RefusesTheLaunch(string field)
    {
        var api = new FakeWindowsJobApi
        {
            ReadBack = limits => field switch
            {
                "breakaway" => limits with { LimitFlags = limits.LimitFlags | WindowsJobLimits.LimitBreakawayOk },
                "silent-breakaway" => limits with { LimitFlags = limits.LimitFlags | WindowsJobLimits.LimitSilentBreakawayOk },
                "memory" => limits with { JobMemoryLimit = limits.JobMemoryLimit - 4096 },
                "processes" => limits with { ActiveProcessLimit = limits.ActiveProcessLimit + 1 },
                "cpu" => limits with { CpuRate = 0 },
                _ => limits with { LimitFlags = limits.LimitFlags & ~WindowsJobLimits.LimitKillOnJobClose }
            }
        };

        var failure = AssertEx.Throws<InvalidOperationException>(() => new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16)));

        AssertEx.Contains(failure.Message, "read back");
        AssertEx.False(api.Calls.Contains("CreateSuspended"), "nothing may be created inside a job whose limits did not read back");
        AssertNothingSurvived(api, resumeAttempted: false);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Dispose_TerminatesTheJob_AndClosesEveryHandle()
    {
        var api = new FakeWindowsJobApi { ExitCodeFor = static _ => null };
        var process = new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16));
        AssertEx.Null(process.ExitCode);

        process.Dispose();

        AssertEx.Equal(1, api.Terminations);
        AssertEx.Equal(0, api.OpenHandles);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Dispose_WhenTheExitCodeCannotBeRead_StillTerminatesTheJob_AndClosesEveryHandle()
    {
        var api = new FakeWindowsJobApi { ExitCodeFails = true };
        var process = new WindowsJobLauncher(api).Start(Command, WindowsJobLimits.Map(2048, 64, 2, 16));
        await process.WaitForExitAsync(CancellationToken.None);

        process.Dispose();

        AssertEx.Equal(1, api.Terminations);
        AssertEx.Equal(0, api.OpenHandles, "the job and every pipe end are closed although the exit code read failed");
    }

    private static void AssertNothingSurvived(FakeWindowsJobApi api, bool resumeAttempted)
    {
        AssertEx.Equal(resumeAttempted, api.Calls.Contains("Resume"), "the payload is resumed only after every check passed");
        AssertEx.True(api.IssuedHandles == 0 || api.Terminations == 1, "a created job is terminated on a failed setup");
        AssertEx.Equal(0, api.OpenHandles, "a failed setup leaves no handle open");
    }
}
