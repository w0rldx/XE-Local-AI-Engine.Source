namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static LinuxTrainingProcessGroupTests;
using OS = TUnit.Core.Enums.OS;

/// <summary>The trainer signal paths over a pidfd: re-verified after the open, sent through the fd, and never a numeric signal on an operational failure.</summary>
/// <remarks>The pidfd calls and <c>kill(2)</c> are scripted; nothing reaches the kernel.</remarks>
[Category(TestCategories.Unit)]
public sealed class LinuxTrainingPidFdSignalTests
{
    private const int Pid = 4242;
    private const long StartTicks = 99;

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillGroup_ThroughThePidFd_VerifiesAfterTheOpen_AndSendsNoNumericSignal()
    {
        var time = new ManualTimeProvider();
        var calls = new FakePidFdCalls();
        var inspector = Inspector(time, calls, _ => calls.Calls.Exists(static call => call.StartsWith("send 15", StringComparison.Ordinal))
            ? TrainingProcessStatRead.Missing
            : Alive(StartTicks));

        var presence = await DriveAsync(inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken), time);

        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
        AssertEx.Equal("open 4242,send 15 group,kill -4242 0", string.Join(',', calls.Calls), "The group is signalled through the fd; only a signal-0 probe is numeric.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kill_WhenThePidCarriesAnotherStartTimeAfterTheOpen_SendsNothing()
    {
        var calls = new FakePidFdCalls();
        var inspector = Inspector(TimeProvider.System, calls, static _ => Alive(StartTicks + 1));

        var presence = await inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken);

        AssertEx.Equal(TrainingProcessPresence.Gone, presence, "A changed start time is a recycled pid.");
        AssertEx.Equal("open 4242", string.Join(',', calls.Calls), "The fd names a stranger: nothing may go through it.");
    }

    /// <summary>A token read and found different, or absent from a non-empty environment, contradicts the receipt.</summary>
    [Test]
    [RunOn(OS.Linux)]
    [Arguments("someone-else")]
    [Arguments("")]
    public async Task Kill_WhenTheRunTokenIsNotTheRecordedOneAfterTheOpen_SendsNothing_AndDefers(string token)
    {
        var calls = new FakePidFdCalls
        {
            ReadRunToken = _ => token
        };
        var inspector = Inspector(TimeProvider.System, calls, static _ => Alive(StartTicks));

        var presence = await inspector.KillProcessAsync(Pid, StartTicks, RunToken);

        AssertEx.Equal(TrainingProcessPresence.Deferred, presence, "An unproven token keeps the receipt for the next startup.");
        AssertEx.Equal("open 4242", string.Join(',', calls.Calls));
    }

    /// <summary>An environment that is unreadable or reads empty (a process past exit_mm) contradicts nothing: the fd and the start time bind the identity.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task KillGroup_WhenTheEnvironmentReadsNoToken_SignalsThroughTheFd()
    {
        var time = new ManualTimeProvider();
        var calls = new FakePidFdCalls
        {
            ReadRunToken = static _ => null
        };
        var inspector = Inspector(time, calls, _ => calls.Calls.Exists(static call => call.StartsWith("send 15", StringComparison.Ordinal))
            ? TrainingProcessStatRead.Missing
            : Alive(StartTicks));

        var presence = await DriveAsync(inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken), time);

        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
        AssertEx.Equal("open 4242,send 15 group,kill -4242 0", string.Join(',', calls.Calls));
    }

    /// <summary>An environment that reads zero bytes belongs to a process without memory and carries no token; only a non-empty one can lack the variable.</summary>
    [Test]
    [Arguments("", null)]
    [Arguments("PATH=/usr/bin\0", "")]
    [Arguments("PATH=/usr/bin\0XE_TRAINING_RUN_TOKEN=token\0", "token")]
    public void RunTokenIn_TellsAnEmptyEnvironmentFromAnAbsentVariable(string environment, string? expected)
    {
        AssertEx.Equal<string?>(expected, LinuxTrainingProcessInspector.RunTokenIn(Encoding.UTF8.GetBytes(environment)));
    }

    /// <summary>A block that fills the buffer is read again, whole, from a fresh open with a doubled buffer: the token at its end is never cut off.</summary>
    [Test]
    [Arguments(3000)]
    [Arguments(4096)]
    public async Task ReadEnvironmentBlock_LargerThanTheFirstBuffer_ReadsTheWholeBlock(int blockBytes)
    {
        using var temp = new TempDirectory("xe-environ");
        var path = temp.FilePath("environ");
        var token = "XE_TRAINING_RUN_TOKEN=token\0";
        var block = Encoding.UTF8.GetBytes(new string('x', blockBytes - token.Length - 1) + "\0" + token);
        await File.WriteAllBytesAsync(path, block);

        var read = LinuxTrainingProcessInspector.ReadEnvironmentBlock(path, initialBytes: 1024, maxBytes: 8192);

        AssertEx.Equal(blockBytes, read?.Length);
        AssertEx.Equal<string?>("token", LinuxTrainingProcessInspector.RunTokenIn(read!));
    }

    /// <summary>A block past the bound, or a file that cannot be opened, is unreadable: null, which contradicts no receipt.</summary>
    [Test]
    [Arguments("too large")]
    [Arguments("missing")]
    public async Task ReadEnvironmentBlock_PastTheBoundOrUnreadable_IsNull(string failure)
    {
        using var temp = new TempDirectory("xe-environ");
        var path = temp.FilePath("environ");
        if (failure == "too large")
        {
            await File.WriteAllBytesAsync(path, new byte[4096]);
        }

        AssertEx.Null(LinuxTrainingProcessInspector.ReadEnvironmentBlock(path, initialBytes: 1024, maxBytes: 4096));
    }

    /// <summary>procfs answers one read with the whole block: this host's own environment, well past the 512 bytes an unknown-length read starts with.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task ReadEnvironmentBlock_OnProcfs_ReadsTheWholeBlock()
    {
        var path = $"/proc/{Environment.ProcessId}/environ";

        var block = LinuxTrainingProcessInspector.ReadEnvironmentBlock(path,
            LinuxTrainingProcessInspector.InitialEnvironmentBytes,
            LinuxTrainingProcessInspector.MaxEnvironmentBytes);

        AssertEx.Equal((await File.ReadAllBytesAsync(path)).Length, block?.Length);
    }

    /// <summary>A zombie's environment is gone with its memory; its start time and the fd still prove it, so its group is signalled as before.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task KillGroup_WhenTheLeaderIsAZombie_SignalsItsGroupWithoutReadingItsToken()
    {
        var calls = new FakePidFdCalls
        {
            ReadRunToken = static _ => throw new InvalidOperationException("A zombie's environment is never read.")
        };
        var inspector = Inspector(TimeProvider.System, calls, static _ => ZombieStat(StartTicks));

        var presence = await inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken);

        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
        AssertEx.Equal("open 4242,send 15 group,kill -4242 0", string.Join(',', calls.Calls));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Kill_WhenTheProcessIsReapedBeforeTheSend_SendsNoNumericSignal()
    {
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, _) => LinuxPidFdSendStatus.Gone
        };
        var inspector = Inspector(TimeProvider.System, calls, static _ => Alive(StartTicks));

        var presence = await inspector.KillProcessAsync(Pid, StartTicks, RunToken);

        AssertEx.Equal(TrainingProcessPresence.Gone, presence, "Gone through the fd, and the group its pid named is empty.");
        AssertEx.Equal("open 4242,send 15,kill -4242 0", string.Join(',', calls.Calls));
    }

    [Test]
    [RunOn(OS.Linux)]
    [Arguments("open")]
    [Arguments("send")]
    public async Task Kill_WhenAPidFdCallFailsOperationally_SendsNoNumericSignal_AndDefers(string failing)
    {
        var calls = new FakePidFdCalls
        {
            OpenStatus = failing == "open" ? LinuxPidFdOpenStatus.Failed : LinuxPidFdOpenStatus.Opened,
            SendResult = static (_, _) => LinuxPidFdSendStatus.Failed
        };
        var inspector = Inspector(TimeProvider.System, calls, static _ => Alive(StartTicks));

        var single = await inspector.KillProcessAsync(Pid, StartTicks, RunToken);
        var group = await inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken);

        AssertEx.Equal(TrainingProcessPresence.Deferred, single);
        AssertEx.Equal(TrainingProcessPresence.Deferred, group);
        AssertEx.Empty(calls.NumericSignals, "EPERM or EMFILE proves nothing, so the unverified numeric signal is never the fallback.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task KillGroup_WhenTheKernelRefusesTheGroupFlag_ProbesThroughTheFd_ThenKillsTheGroupByNumber()
    {
        var time = new ManualTimeProvider();
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent
        };
        var inspector = Inspector(time, calls, _ => calls.NumericSignals.Any() ? TrainingProcessStatRead.Missing : Alive(StartTicks));

        var presence = await DriveAsync(inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken), time);

        AssertEx.Equal(TrainingProcessPresence.Gone, presence);
        AssertEx.Equal("open 4242,send 15 group,send 0,kill -4242 15,kill -4242 0", string.Join(',', calls.Calls),
            "The probe through the fd comes first; only then the numeric group kill.");
    }

    /// <summary>A successful probe proves only that the fd's process lives; the refused numeric kill that follows is the outcome, so the receipt is kept.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task KillGroup_WhenTheGroupFlagIsRefused_AndTheNumericKillIsRefusedToo_Defers()
    {
        const int eperm = 1;
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent,
            KillErrno = eperm
        };
        var time = new ManualTimeProvider();
        var inspector = Inspector(time, calls, static _ => Alive(StartTicks));

        var presence = await DriveAsync(inspector.KillProcessGroupAsync(Pid, StartTicks, RunToken), time);

        AssertEx.Equal(TrainingProcessPresence.Deferred, presence, "Nothing reached the trainer, so nothing about it was learned.");
        AssertEx.Equal("open 4242,send 15 group,send 0,kill -4242 15", string.Join(',', calls.Calls), "A refused SIGTERM is never followed by a wait and a SIGKILL.");
    }

    [Test]
    public void PinTrainer_WhenPidFdIsUnsupported_KeepsTheNumericPath()
    {
        var calls = new FakePidFdCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Unsupported
        };

        var pin = Pin(calls, static _ => false);

        AssertEx.Equal(TrainerPidFdState.Legacy, pin.State);
        AssertEx.Null(pin.PidFd);
        AssertEx.True(calls.Opened.Single().IsClosed);
    }

    [Test]
    public void PinTrainer_WhenTheOpenFailsOperationally_LeavesEachSignalToVerifyItself()
    {
        var calls = new FakePidFdCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Failed
        };

        var pin = Pin(calls, static _ => false);

        AssertEx.Equal(TrainerPidFdState.Unpinned, pin.State);
        AssertEx.True(calls.Opened.Single().IsClosed);
    }

    [Test]
    public void PinTrainer_WhenNoProcessHoldsThePid_RefusesEverySignal()
    {
        var calls = new FakePidFdCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Gone
        };

        AssertEx.Equal(TrainerPidFdState.Refused, Pin(calls, static _ => false).State);
    }

    [Test]
    public void PinTrainer_WhenTheChildRunsAndCarriesTheRunToken_PinsTheFd()
    {
        var calls = new FakePidFdCalls();

        var pin = Pin(calls, static _ => false);

        AssertEx.Equal(TrainerPidFdState.Pinned, pin.State);
        AssertEx.True(ReferenceEquals(calls.Opened.Single(), pin.PidFd), "The handle owns the very fd the open handed out.");
        AssertEx.False(pin.PidFd!.IsClosed);
    }

    /// <summary>An exited child may already be reaped and its pid reused, so the fd may name a stranger.</summary>
    [Test]
    public void PinTrainer_WhenTheChildHasExited_DisposesTheFdAndRefuses()
    {
        var calls = new FakePidFdCalls();

        var pin = Pin(calls, static _ => true);

        AssertEx.Equal(TrainerPidFdState.Refused, pin.State);
        AssertEx.Null(pin.PidFd);
        AssertEx.True(calls.Opened.Single().IsClosed);
    }

    /// <summary>In reap-all mode the runtime can report a reaped child running; the run token still tells a stranger apart.</summary>
    [Test]
    public void PinTrainer_WhenThePidCarriesAnotherRunToken_DisposesTheFdAndRefuses()
    {
        var calls = new FakePidFdCalls
        {
            ReadRunToken = static _ => "someone-else"
        };

        var pin = Pin(calls, static _ => false);

        AssertEx.Equal(TrainerPidFdState.Refused, pin.State);
        AssertEx.True(calls.Opened.Single().IsClosed);
    }

    [Test]
    public void PinTrainer_WhenTheTokenAppearsAfterTheExec_RetriesWithinTheWindow()
    {
        var reads = 0;
        var calls = new FakePidFdCalls
        {
            ReadRunToken = _ => ++reads < 3 ? string.Empty : RunToken
        };

        var pin = Pin(calls, static _ => false);

        AssertEx.Equal(TrainerPidFdState.Pinned, pin.State);
        AssertEx.Equal(expected: 3, reads);
    }

    [Test]
    public void PinTrainer_WhenTheEnvironmentStaysUnreadable_PinsOnTheRuntimesWordAtTheTimeout()
    {
        var time = new ManualTimeProvider();
        var start = time.GetUtcNow();
        var calls = new FakePidFdCalls
        {
            ReadRunToken = static _ => null
        };

        var pin = LinuxTrainingProcessSpawner.PinTrainer(Pid, RunToken, calls.Open, calls.ReadRunToken, interval =>
        {
            time.Advance(interval);
            return false;
        }, time, NullLogger.Instance);

        AssertEx.Equal(TrainerPidFdState.Pinned, pin.State);
        AssertEx.True(time.GetUtcNow() - start >= LinuxTrainingProcessSpawner.GroupLeaderTimeout, "The read is retried for the whole window first.");
    }

    [Test]
    public void AwaitTrainerIdentity_WhenThePidCarriesAnotherRunToken_TrustsNoSample()
    {
        var time = new ManualTimeProvider();

        var (stat, leadsGroup, executable) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            static _ => new TrainingProcessStat(Pid, StartTicks),
            static _ => "/opt/uv/python/bin/python3.14",
            static _ => "someone-else",
            RunToken,
            Setsid,
            interval =>
            {
                time.Advance(interval);
                return false;
            },
            time);

        AssertEx.Null(stat, "A sample read beside another run token describes a stranger.");
        AssertEx.False(leadsGroup);
        AssertEx.Null(executable);
    }

    /// <summary>An unreadable environment contradicts nothing, as at the pin: the sample read beside it is kept.</summary>
    [Test]
    public void AwaitTrainerIdentity_WhenTheEnvironmentIsUnreadable_KeepsTheSample()
    {
        var time = new ManualTimeProvider();

        var (stat, leadsGroup, executable) = LinuxTrainingProcessSpawner.AwaitTrainerIdentity(Pid,
            static _ => new TrainingProcessStat(Pid, StartTicks),
            static _ => "/opt/uv/python/bin/python3.14",
            static _ => null,
            RunToken,
            Setsid,
            interval =>
            {
                time.Advance(interval);
                return false;
            },
            time);

        AssertEx.Equal(new TrainingProcessStat(Pid, StartTicks), stat);
        AssertEx.True(leadsGroup);
        AssertEx.Equal("/opt/uv/python/bin/python3.14", executable);
    }

    private static LinuxTrainingProcessInspector Inspector(TimeProvider time, FakePidFdCalls calls, Func<int, TrainingProcessStatRead> readStat) =>
        new(time, NullLogger<LinuxTrainingProcessInspector>.Instance, readStat, calls.Kill, calls.Ops);

    private static LinuxTrainingProcessSpawner.TrainerPin Pin(FakePidFdCalls calls, Func<TimeSpan, bool> waitForExit) =>
        LinuxTrainingProcessSpawner.PinTrainer(Pid, RunToken, calls.Open, calls.ReadRunToken, waitForExit, new ManualTimeProvider(), NullLogger.Instance);
}
