namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static LinuxTrainingProcessGroupTests;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     <see cref="ITrainingProcessHandle.RequestStop" />, <see cref="ITrainingProcessHandle.KillGroup" /> and <see cref="IDisposable.Dispose" /> in every
///     pidfd state a spawn can leave, on a real child that carries the run token. The pidfd sends and <c>kill(2)</c> are scripted, so nothing is signalled.
/// </summary>
[Category(TestCategories.Integration)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class LinuxTrainingProcessHandlePidFdTests
{
    [Test]
    public void Pinned_SignalsOnlyThroughTheFd_AndDisposeClosesIt()
    {
        var calls = new FakePidFdCalls();
        using var child = new TokenChild();
        var pidFd = calls.Open(child.Process.Id);
        calls.Calls.Clear();
        var handle = child.Handle(calls, new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Pinned, pidFd));

        handle.RequestStop();
        handle.KillGroup();

        AssertEx.Equal("send 15 group,send 15 group,send 9 group", string.Join(',', calls.Calls), "No signal leaves the fd, and no fd is opened again.");
        child.End();
        handle.Dispose();
        AssertEx.True(pidFd.IsClosed, "The handle owns the fd and closes it on dispose.");
    }

    [Test]
    public void Pinned_WhenASendFails_SendsNoNumericSignal()
    {
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, _) => LinuxPidFdSendStatus.Failed
        };
        using var child = new TokenChild();
        using var handle = child.Handle(calls, new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Pinned, calls.Open(child.Process.Id)));

        handle.KillGroup();

        AssertEx.Empty(calls.NumericSignals, "EPERM or EMFILE proves nothing about the pid, so the number is never signalled instead.");
        child.End();
    }

    [Test]
    public void Pinned_WhenTheKernelRefusesTheGroupFlag_ProbesThroughTheFd_ThenKillsTheGroupByNumber()
    {
        var calls = new FakePidFdCalls
        {
            SendResult = static (_, group) => group ? LinuxPidFdSendStatus.GroupFlagUnsupported : LinuxPidFdSendStatus.Sent
        };
        using var child = new TokenChild();
        var pidFd = calls.Open(child.Process.Id);
        calls.Calls.Clear();
        using var handle = child.Handle(calls, new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Pinned, pidFd));

        handle.RequestStop();

        AssertEx.Equal($"send 15 group,send 0,kill {-child.Process.Id} 15", string.Join(',', calls.Calls));
        child.End();
    }

    [Test]
    public void Refused_NeverSignalsAnything()
    {
        var calls = new FakePidFdCalls();
        using var child = new TokenChild();
        var handle = child.Handle(calls, new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Refused, PidFd: null), waitForExit: static _ => false);

        handle.RequestStop();
        handle.KillGroup();
        handle.Dispose();

        AssertEx.Empty(calls.Calls, "A pid the spawn could not prove is this child is never signalled, through an fd or by number.");
        child.End();
    }

    [Test]
    public void Unpinned_EachSignalOpensAnFd_AndVerifiesTheChildThroughIt()
    {
        var calls = new FakePidFdCalls
        {
            ReadRunToken = LinuxTrainingProcessInspector.ReadRunToken
        };
        using var child = new TokenChild();
        using var handle = child.Handle(calls, new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Unpinned, PidFd: null));

        handle.RequestStop();
        handle.KillGroup();

        var pid = child.Process.Id;
        AssertEx.Equal($"open {pid},send 15 group,open {pid},send 15 group,open {pid},send 9 group", string.Join(',', calls.Calls),
            "The real start time and the real run token matched after every open.");
        AssertEx.True(calls.Opened.TrueForAll(static pidFd => pidFd.IsClosed), "Each signal's fd is closed with the call.");
        child.End();
    }

    [Test]
    [Arguments("start time")]
    [Arguments("run token")]
    public void Unpinned_WhenTheReceiptNoLongerMatchesAfterTheOpen_SendsNothing(string mismatch)
    {
        var calls = new FakePidFdCalls
        {
            ReadRunToken = LinuxTrainingProcessInspector.ReadRunToken
        };
        using var child = new TokenChild(runToken: mismatch == "run token" ? "another-run" : RunToken);
        using var handle = child.Handle(calls,
            new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Unpinned, PidFd: null),
            startTicksOffset: mismatch == "start time" ? 1 : 0);

        handle.RequestStop();

        AssertEx.Equal($"open {child.Process.Id}", string.Join(',', calls.Calls));
        child.End();
    }

    [Test]
    public void Unpinned_WhenTheOpenFailsAgain_SendsNoNumericSignal()
    {
        var calls = new FakePidFdCalls
        {
            OpenStatus = LinuxPidFdOpenStatus.Failed
        };
        using var child = new TokenChild();
        using var handle = child.Handle(calls, new LinuxTrainingProcessSpawner.TrainerPin(TrainerPidFdState.Unpinned, PidFd: null));

        handle.KillGroup();

        AssertEx.Empty(calls.NumericSignals);
        child.End();
    }

    /// <summary>A setsid sleep whose environment carries a run token, so the real <c>/proc</c> reads verify it, ended by its own process handle.</summary>
    private sealed class TokenChild : IDisposable
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly OwnedProcess _owned;

        public TokenChild(string runToken = RunToken)
        {
            if (!File.Exists(Setsid))
            {
                Skip.Test($"BLOCKED: {Setsid} is absent, so the child cannot lead a group of its own.");
            }

            var startInfo = new ProcessStartInfo(Setsid)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("/bin/sleep");
            startInfo.ArgumentList.Add("30");
            startInfo.Environment[LinuxTrainingProcessInspector.RunTokenVariable] = runToken;
            Process = LinuxTrainingProcessSpawner.StartStreaming(startInfo, _output.Writer);
            _owned = new OwnedProcess
            {
                ProcessId = Process.Id,
                StartTicks = LinuxTrainingProcessInspector.ReadStatOrNull(Process.Id)?.StartTicks
                             ?? throw new InvalidOperationException("The child exited before its start time could be read.")
            };
        }

        public Process Process { get; }

        /// <summary>A handle over the child, which signals only through <paramref name="calls" />; by default the SIGTERM grace runs out at once.</summary>
        public LinuxTrainingProcessHandle Handle(FakePidFdCalls calls,
            LinuxTrainingProcessSpawner.TrainerPin pin,
            Func<TimeSpan, bool>? waitForExit = null,
            long startTicksOffset = 0) =>
            new(Process,
                new TrainingLaunchReceipt
                {
                    Pid = Process.Id,
                    Pgid = Process.Id,
                    ExecutablePath = null,
                    StartTicks = _owned.StartTicks + startTicksOffset,
                    RunToken = RunToken
                },
                _output,
                NullLogger.Instance,
                TimeProvider.System,
                calls.Kill,
                waitForExit ?? (static _ => false),
                pin,
                calls.Ops);

        /// <summary>Ends the child through its own process handle, or by its recorded start time once the handle under test disposed that.</summary>
        public void End()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill();
                }

                Process.WaitForExit();
            }
            catch (InvalidOperationException)
            {
                _owned.KillIfStillOurs();
            }
        }

        public void Dispose()
        {
            End();
            Process.Dispose();
        }
    }
}
