namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static LinuxTrainingProcessGroupTests;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     A trainer whose receipt proves no group of its own, started the way the spawner streams it but without setsid, which the
///     spawner always runs and so cannot leave unconfirmed on demand.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class LinuxTrainingProcessHandleUnconfirmedGroupTests
{
    /// <summary>With no group to probe, the wait keeps the output EOF: a child holding the pipe keeps the exit unverified.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task WaitForGroupExit_WhenTheGroupIsUnconfirmed_WaitsForTheOutputToClose()
    {
        // Without setsid the trainer sits in the test host's group, and its receipt records no group of its own.
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("/bin/sleep 30 & echo $!; exit 0");
        var output = Channel.CreateUnbounded<string>();
        OwnedProcess? child = null;
#pragma warning disable CA2000 // The handle owns the process and disposes it.
        var process = LinuxTrainingProcessSpawner.StartStreaming(startInfo, output.Writer);
        using var handle = new LinuxTrainingProcessHandle(process,
            new TrainingLaunchReceipt
            {
                Pid = process.Id,
                // What a spawn records when the trainer never confirmed a group of its own.
                Pgid = 0,
                ExecutablePath = null,
                StartTicks = 0,
                RunToken = "token"
            },
            output,
            NullLogger.Instance,
            TimeProvider.System);
#pragma warning restore CA2000
        try
        {
            AssertEx.False(TrainingProcessGroupGuard.MaySignalGroup(handle.Receipt.Pid, handle.Receipt.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId));
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            child = await ReadChildAsync(handle, budget.Token);
            using var bound = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            AssertEx.Null(await handle.WaitForGroupExitAsync(bound.Token), "The child still holds the pipe, so the exit stays unverified.");

            child.KillIfStillOurs();
            AssertEx.Equal<int?>(0, await handle.WaitForGroupExitAsync(budget.Token), "The pipe closed with the child, so the exit is verified.");
        }
        finally
        {
            child?.KillIfStillOurs();
        }
    }

    /// <summary>
    ///     A receipt read before setsid ran records the host's group, yet the trainer then leads one named by its pid: a child left there with its
    ///     pipes closed sends no output EOF to wait for, so the exit stays unverified until that group is empty.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task WaitForGroupExit_WhenAPreSetsidReceiptsTrainerLeavesAChildWithClosedPipes_WaitsForItsGroupToEmpty()
    {
        if (!File.Exists(Setsid))
        {
            Skip.Test($"BLOCKED: {Setsid} is absent, so the trainer cannot lead a group of its own.");
        }

        // setsid(1) execs in place for a child that does not lead a group, so the trainer keeps its pid and leads the group it names.
        var startInfo = new ProcessStartInfo(Setsid)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("/bin/sleep 30 </dev/null >/dev/null 2>&1 & echo $!; exit 0");
        var output = Channel.CreateUnbounded<string>();
        OwnedProcess? child = null;
#pragma warning disable CA2000 // The handle owns the process and disposes it.
        var process = LinuxTrainingProcessSpawner.StartStreaming(startInfo, output.Writer);
        using var handle = new LinuxTrainingProcessHandle(process,
            new TrainingLaunchReceipt
            {
                Pid = process.Id,
                // What a spawn records when it read the trainer's group before setsid ran.
                Pgid = LinuxTrainingProcessInspector.HostProcessGroupId,
                ExecutablePath = null,
                StartTicks = 0,
                RunToken = "token"
            },
            output,
            NullLogger.Instance,
            TimeProvider.System);
#pragma warning restore CA2000
        try
        {
            AssertEx.False(TrainingProcessGroupGuard.MaySignalGroup(handle.Receipt.Pid, handle.Receipt.Pgid, LinuxTrainingProcessInspector.HostProcessGroupId));
            using var budget = new CancellationTokenSource(TestBudgets.Contended);
            child = await ReadChildAsync(handle, budget.Token);
            AssertEx.Equal(handle.Receipt.Pid, LinuxTrainingProcessInspector.ReadStatOrNull(child.ProcessId)?.Pgid, "The child sits in the group the trainer leads.");
            await AssertEx.EventuallyAsync(() => LinuxTrainingProcessInspector.ReadStat(handle.Receipt.Pid).Presence == TrainingProcessPresence.Gone,
                TestBudgets.Contended,
                "The trainer itself exits at once.");
            using var bound = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            AssertEx.Null(await handle.WaitForGroupExitAsync(bound.Token), "The output closed with the trainer, but its group still has a member.");
            AssertEx.True(LinuxTrainingProcessInspector.MaySignal(LinuxTrainingProcessInspector.ReadStat(child.ProcessId), child.StartTicks)
                          && LinuxTrainingProcessInspector.ReadStat(child.ProcessId).Presence == TrainingProcessPresence.Alive,
                "The child outlived the bound, so the unverified exit was its doing.");

            // Positive control: once the last member is gone, the same wait verifies the exit.
            child.KillIfStillOurs();
            AssertEx.Equal<int?>(0, await handle.WaitForGroupExitAsync(budget.Token), "The emptied group verifies the trainer's exit.");
        }
        finally
        {
            child?.KillIfStillOurs();
        }
    }
}
