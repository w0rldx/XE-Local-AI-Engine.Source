namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     A recording <see cref="IWindowsJobLaunchNative" />: processes live until terminated, the helper's handle table is a set of parked
///     values, and any numbered call can be made to fail or run a callback first.
/// </summary>
internal sealed class FakeWindowsJobLaunchNative : IWindowsJobLaunchNative
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<FakeWindowsJobApi.FakeHandle> _handles = [];
    private readonly HashSet<SafeHandle> _alive = [];
    private readonly HashSet<SafeHandle> _held = [];
    private IntPtr _nextParked = 0x1000;
    private int _nextProcessId = 100;

    /// <summary>Every call, as "Name#n" with n counting that name from 1.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The calls that throw a <see cref="Win32Exception" /> instead of acting, by their <see cref="Calls" /> entry.</summary>
    public HashSet<string> FailAt { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs before the named call acts, with the call's entry; used to change the world inside a launch.</summary>
    public Action<string>? Before { get; set; }

    public bool HelperIsMember { get; set; } = true;

    /// <summary>Every wait fails, as a WAIT_FAILED would.</summary>
    public bool WaitsFail { get; set; }

    /// <summary>The values parked in the helper and not yet closed there.</summary>
    public HashSet<IntPtr> HelperTable { get; } = [];

    /// <summary>The engine ends handed to <see cref="Park" />, in order.</summary>
    public List<SafeHandle> ParkedEnds { get; } = [];

    /// <summary>The values each <see cref="CreateCommand" /> listed, in order.</summary>
    public List<IntPtr[]> Listed { get; } = [];

    public List<SafeHandle> Helpers { get; } = [];

    public List<SafeHandle> Commands { get; } = [];

    public List<SafeHandle> Threads { get; } = [];

    public SafeHandle? ParentJob { get; private set; }

    public int ParentJobsCreated { get; private set; }

    public bool IsAlive(SafeHandle process) => _alive.Contains(process);

    /// <summary>The process ends on its own, as a helper killed from outside would.</summary>
    public void Exit(SafeHandle process) => _alive.Remove(process);

    /// <summary>A terminate of this process fails and it stays alive until <see cref="Release" />.</summary>
    public void Hold(SafeHandle process) => _held.Add(process);

    public void Release(SafeHandle process) => _held.Remove(process);

    public SafeHandle CreateParentJob()
    {
        Step(nameof(CreateParentJob));
        ParentJobsCreated++;
        ParentJob = Issue("parent-job");
        return ParentJob;
    }

    public WindowsJobCreatedProcess CreateHelper(SafeHandle parentJob)
    {
        Step(nameof(CreateHelper));
        var created = Spawn("helper");
        Helpers.Add(created.Process);
        return created;
    }

    public IntPtr Park(SafeHandle helper, SafeHandle end)
    {
        Step(nameof(Park));
        ObjectDisposedException.ThrowIf(end.IsClosed, end);
        ParkedEnds.Add(end);
        var value = _nextParked;
        _nextParked += 4;
        HelperTable.Add(value);
        return value;
    }

    public WindowsJobCreatedProcess CreateCommand(SafeHandle job, SafeHandle helper, WindowsJobStartInfo startInfo, ReadOnlySpan<IntPtr> parked)
    {
        Step(nameof(CreateCommand));
        Listed.Add(parked.ToArray());
        var created = Spawn("command");
        Commands.Add(created.Process);
        return created;
    }

    public void Unpark(SafeHandle helper, IntPtr parked)
    {
        // DUPLICATE_CLOSE_SOURCE closes the value whatever the result, so a failed close still empties the slot.
        HelperTable.Remove(parked);
        Step(nameof(Unpark));
    }

    public bool IsProcessInJob(SafeHandle process, SafeHandle job)
    {
        Step(nameof(IsProcessInJob));
        return HelperIsMember;
    }

    public void TerminateProcess(SafeHandle process)
    {
        Step(nameof(TerminateProcess));
        ObjectDisposedException.ThrowIf(process.IsClosed, process);
        if (_held.Contains(process))
        {
            throw new Win32Exception(5, "injected: the process could not be ended");
        }

        _alive.Remove(process);
    }

    public WindowsJobExitWait Wait(SafeHandle process, TimeSpan timeout, out int error)
    {
        Step(nameof(Wait));
        ObjectDisposedException.ThrowIf(process.IsClosed, process);
        error = WaitsFail ? 6 : 0;
        if (WaitsFail)
        {
            return WindowsJobExitWait.Failed;
        }

        return _alive.Contains(process) ? WindowsJobExitWait.TimedOut : WindowsJobExitWait.Signalled;
    }

    /// <summary>Whether this many calls of <paramref name="name" /> were made.</summary>
    public int Count(string name) => _counts.GetValueOrDefault(name);

    public int OpenHandles => _handles.Count(static handle => !handle.IsClosed);

    private WindowsJobCreatedProcess Spawn(string kind)
    {
        var process = Issue(kind);
        var thread = Issue(kind + "-thread");
        Threads.Add(thread);
        _alive.Add(process);
        return new WindowsJobCreatedProcess
        {
            Process = process,
            Thread = thread,
            ProcessId = _nextProcessId++
        };
    }

    private FakeWindowsJobApi.FakeHandle Issue(string kind)
    {
        var handle = new FakeWindowsJobApi.FakeHandle(kind);
        _handles.Add(handle);
        return handle;
    }

    private void Step(string name)
    {
        var number = _counts.GetValueOrDefault(name) + 1;
        _counts[name] = number;
        var entry = $"{name}#{number}";
        Calls.Add(entry);
        Before?.Invoke(entry);
        if (FailAt.Contains(entry))
        {
            throw new Win32Exception(31, $"injected failure at {entry}");
        }
    }
}
