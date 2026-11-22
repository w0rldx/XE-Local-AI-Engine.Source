// The Desktop shell compiles this file as linked source under its own namespace (it has no engine references).
#if XE_DESKTOP
namespace XE_Local_AI_Engine.Desktop.Linux;
#else
namespace XE_Local_AI_Engine.Providers.Abstractions;
#endif

using System.Globalization;

/// <summary>The kernel and procfs calls and the bounds <see cref="LinuxProcessTreeKill" /> uses; tests replace them to script a process table.</summary>
internal sealed class LinuxProcessTreeKillCalls
{
    public static readonly LinuxProcessTreeKillCalls Real = new();

    /// <summary>True when this host is not PID 1 and <c>/proc</c> numbers its own PID namespace, so a procfs pid is a pid this host can open.</summary>
    public Func<bool> HostVerifiable { get; init; } = IsHostVerifiable;

    public Func<int, LinuxPidFd> Open { get; init; } = LinuxPidFd.Open;

    /// <summary>Sends a signal to the one process an fd names.</summary>
    public Func<LinuxPidFd, int, LinuxPidFdSendResult> Send { get; init; } = static (pidFd, signal) => pidFd.Send(signal, group: false);

    /// <summary>Every thread-group leader <c>/proc</c> lists; throws when <c>/proc</c> cannot be listed.</summary>
    public Func<IEnumerable<int>> ListProcesses { get; init; } = static () => NumericEntries("/proc") ?? throw new IOException("/proc cannot be listed.");

    public Func<int, LinuxProcStat> ReadStat { get; init; } =
        static pid => LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/stat"));

    /// <summary>The thread ids under <c>/proc/[pid]/task</c>; null when the directory is missing or unreadable.</summary>
    public Func<int, IReadOnlyList<int>?> ListThreads { get; init; } =
        static pid => NumericEntries(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/task"));

    public Func<int, int, LinuxProcStat> ReadThreadStat { get; init; } =
        static (pid, tid) => LinuxProcStat.Read(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/task/{tid}/stat"));

    /// <summary>Polls a condition until it holds or the timeout passes; true when it held.</summary>
    public Func<Func<bool>, TimeSpan, bool> WaitUntil { get; init; } = static (condition, timeout) => SpinWait.SpinUntil(condition, timeout);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>How long one SIGSTOP may take to show on every thread; a thread in uninterruptible sleep holds it up.</summary>
    public TimeSpan StopConfirmBound { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>The whole walk's budget; the SIGKILL cleanup runs after it whatever is left.</summary>
    public TimeSpan TotalBound { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The most processes one walk pins, which bounds the fds it holds.</summary>
    public int MaxMembers { get; init; } = 512;

    private static bool IsHostVerifiable()
    {
        if (Environment.ProcessId == 1)
        {
            // As PID 1 the runtime reaps every child with waitpid(-1), which can reap before Process.HasExited flips.
            return false;
        }

        try
        {
            return new DirectoryInfo("/proc/self").LinkTarget == Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<int>? NumericEntries(string directory)
    {
        try
        {
            var ids = new List<int>();
            foreach (var entry in new DirectoryInfo(directory).EnumerateDirectories())
            {
                if (int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
