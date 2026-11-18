namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>Scripted pidfd calls and <c>kill(2)</c> that record every call, in order, into one log; nothing reaches the kernel.</summary>
internal sealed class FakePidFdCalls
{
    private const int Esrch = 3;

    /// <summary>"open 4242", "send 15 group", "send 0", "kill -4242 0": every call in the order it was made.</summary>
    public List<string> Calls { get; } = [];

    public LinuxPidFdOpenStatus OpenStatus { get; init; } = LinuxPidFdOpenStatus.Opened;

    /// <summary>What each send answers, given the signal and whether it targets the group; sent by default.</summary>
    public Func<int, bool, LinuxPidFdSendStatus> SendResult { get; init; } = static (_, _) => LinuxPidFdSendStatus.Sent;

    /// <summary>What a numeric <c>kill(2)</c> of a non-zero signal fails with; 0, accepted, by default.</summary>
    public int KillErrno { get; init; }

    /// <summary>What the environment read answers; this trainer's run token by default.</summary>
    public Func<int, string?> ReadRunToken { get; init; } = static _ => LinuxTrainingProcessGroupTests.RunToken;

    /// <summary>Every fd an open handed out, so a test can check who closed it.</summary>
    public List<LinuxPidFd> Opened { get; } = [];

    public TrainerPidFdOps Ops =>
        new()
        {
            Open = Open,
            Send = Send,
            ReadRunToken = ReadRunToken
        };

    /// <summary>The non-zero numeric signals sent: the calls a pinned or verified path must never make.</summary>
    public IEnumerable<string> NumericSignals =>
        Calls.Where(static call => call.StartsWith("kill ", StringComparison.Ordinal) && !call.EndsWith(" 0", StringComparison.Ordinal));

    public LinuxPidFd Open(int pid)
    {
        Calls.Add($"open {pid}");
        // A made-up fd the handle never owns, so disposing it closes nothing real.
        var pidFd = new LinuxPidFd(preexistingHandle: OpenStatus == LinuxPidFdOpenStatus.Opened ? 4096 : -1, ownsHandle: false, OpenStatus);
        Opened.Add(pidFd);
        return pidFd;
    }

    public LinuxPidFdSendResult Send(LinuxPidFd pidFd, int signal, bool group)
    {
        Calls.Add(group ? $"send {signal} group" : $"send {signal}");
        return new LinuxPidFdSendResult(SendResult(signal, group), Errno: 0);
    }

    /// <summary>Records each call; every signal-0 group probe answers ESRCH, an empty group, and every other signal <see cref="KillErrno" />.</summary>
    public int Kill(int target, int signal)
    {
        Calls.Add($"kill {target} {signal}");
        var errno = signal == 0 ? Esrch : KillErrno;
        if (errno == 0)
        {
            return 0;
        }

        Marshal.SetLastPInvokeError(errno);
        return -1;
    }
}
