namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     Forwards every call to the real <see cref="WindowsJobLaunchNative" />, keeps its own handles to every helper, command and parent
///     job it sees, records the values crossing the seam, and fails or holds a chosen call on request.
/// </summary>
/// <remarks>
///     A <see cref="Before" /> throw is that call's failure and the native call is not made. While <see cref="HoldCommands" /> is set a
///     terminate of a created command throws and leaves it running, so a confirmed exit can be withheld.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class RecordingLaunchNative : IWindowsJobLaunchNative, IDisposable
{
    // SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE | PROCESS_DUP_HANDLE, and JOB_OBJECT_QUERY.
    private const uint ProcessAccess = 0x0010_0000 | 0x1000 | 0x0001 | 0x0040;
    private const uint JobQuery = 0x0004;

    private readonly WindowsJobLaunchNative _real = new();
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly HashSet<int> _held = [];
    private readonly List<KeyValuePair<SafeWaitHandle, IntPtr>> _reservations = [];
    private readonly List<EventWaitHandle> _reservationEvents = [];

    /// <summary>Runs before each call with its entry, "Name#n" counting that name from 1.</summary>
    public Action<string>? Before { get; set; }

    /// <summary>A parked value equal to any engine end seen so far is replaced (R3-C3), its slot reserved with an unrelated event.</summary>
    public bool AvoidEngineValues { get; set; }

    public bool HoldCommands { get; set; }

    public List<string> Calls { get; } = [];

    public List<SafeWaitHandle> Helpers { get; } = [];

    public List<SafeWaitHandle> Commands { get; } = [];

    public List<int> CommandIds { get; } = [];

    public SafeWaitHandle? ParentJob { get; private set; }

    /// <summary>The product's own handles, as passed in: the parent job and the last helper.</summary>
    public SafeHandle? ParentJobArgument { get; private set; }

    public SafeHandle? HelperArgument { get; private set; }

    public List<IntPtr> EngineEnds { get; } = [];

    public List<IntPtr> ParkedValues { get; } = [];

    /// <summary>The values each <c>CreateCommand</c> was handed, which it puts in both the handle list and <c>hStd*</c>.</summary>
    public List<IntPtr[]> Listed { get; } = [];

    public int ParentJobsCreated { get; private set; }

    public void ReleaseHolds() => _held.Clear();

    /// <summary>Closes the reservation events' duplicates in the helper and the events themselves.</summary>
    public void ReleaseReservations()
    {
        foreach (var (helper, value) in _reservations)
        {
            _ = WindowsJobNative.DuplicateHandle(helper, value, IntPtr.Zero, IntPtr.Zero, 0, bInheritHandle: false, WindowsJobNative.DuplicateCloseSource);
        }

        _reservations.Clear();
        foreach (var reservation in _reservationEvents)
        {
            reservation.Dispose();
        }

        _reservationEvents.Clear();
    }

    /// <summary>Closes only this forwarder's copy of the parent job.</summary>
    public void CloseParentJobCopy() => ParentJob?.Dispose();

    public SafeHandle CreateParentJob()
    {
        Step(nameof(CreateParentJob));
        var job = _real.CreateParentJob();
        ParentJobsCreated++;
        ParentJob = Duplicate(job, JobQuery);
        return job;
    }

    public WindowsJobCreatedProcess CreateHelper(SafeHandle parentJob)
    {
        Step(nameof(CreateHelper));
        ParentJobArgument = parentJob;
        var created = _real.CreateHelper(parentJob);
        Helpers.Add(Duplicate(created.Process, ProcessAccess));
        return created;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar Code Smell",
        "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification = "Only the value is recorded, to compare with the parked values; it is never used as a handle.")]
    public IntPtr Park(SafeHandle helper, SafeHandle end)
    {
        Step(nameof(Park));
        HelperArgument = helper;
        EngineEnds.Add(end.DangerousGetHandle());
        var parked = _real.Park(helper, end);
        var attempts = 0;
        while (AvoidEngineValues && EngineEnds.Contains(parked))
        {
            if (++attempts > 16)
            {
                throw new InvalidOperationException("the helper kept handing out values equal to engine handles");
            }

            _real.Unpark(helper, parked);
            Reserve(helper);
            parked = _real.Park(helper, end);
        }

        ParkedValues.Add(parked);
        return parked;
    }

    public WindowsJobCreatedProcess CreateCommand(SafeHandle job, SafeHandle helper, WindowsJobStartInfo startInfo, ReadOnlySpan<IntPtr> parked)
    {
        Step(nameof(CreateCommand));
        HelperArgument = helper;
        Listed.Add(parked.ToArray());
        var created = _real.CreateCommand(job, helper, startInfo, parked);
        Commands.Add(Duplicate(created.Process, ProcessAccess));
        CommandIds.Add(created.ProcessId);
        if (HoldCommands)
        {
            _held.Add(created.ProcessId);
        }

        return created;
    }

    public void Unpark(SafeHandle helper, IntPtr parked)
    {
        Step(nameof(Unpark));
        _real.Unpark(helper, parked);
    }

    public bool IsProcessInJob(SafeHandle process, SafeHandle job)
    {
        Step(nameof(IsProcessInJob));
        return _real.IsProcessInJob(process, job);
    }

    public void TerminateProcess(SafeHandle process)
    {
        Step(nameof(TerminateProcess));
        if (_held.Contains(ProcessId(process)))
        {
            throw new Win32Exception(5, "the test is withholding this process's exit");
        }

        _real.TerminateProcess(process);
    }

    public WindowsJobExitWait Wait(SafeHandle process, TimeSpan timeout, out int error)
    {
        Step(nameof(Wait));
        return _real.Wait(process, timeout, out error);
    }

    public int Count(string name) => _counts.GetValueOrDefault(name);

    public void Dispose()
    {
        ReleaseReservations();
        foreach (var handle in Helpers.Concat(Commands))
        {
            handle.Dispose();
        }

        ParentJob?.Dispose();
    }

    public static int ProcessId(SafeHandle process) => checked((int)GetProcessId(process));

    private void Step(string name)
    {
        var number = _counts.GetValueOrDefault(name) + 1;
        _counts[name] = number;
        var entry = $"{name}#{number}";
        Calls.Add(entry);
        Before?.Invoke(entry);
    }

    private void Reserve(SafeHandle helper)
    {
        var reservation = new EventWaitHandle(false, EventResetMode.ManualReset);
        _reservationEvents.Add(reservation);
        if (!WindowsJobNative.DuplicateHandle(WindowsJobNative.GetCurrentProcess(),
                reservation.SafeWaitHandle,
                helper,
                out var value,
                0,
                bInheritHandle: false,
                WindowsJobNative.DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Released through this forwarder's own copy of the helper, which outlives the product's handle.
        _reservations.Add(new KeyValuePair<SafeWaitHandle, IntPtr>(Helpers[^1], value));
    }

    private static SafeWaitHandle Duplicate(SafeHandle handle, uint access)
    {
        if (!WindowsJobNative.DuplicateHandle(WindowsJobNative.GetCurrentProcess(),
                handle,
                WindowsJobNative.GetCurrentProcess(),
                out var copy,
                access,
                bInheritHandle: false,
                dwOptions: 0))
        {
            var error = Marshal.GetLastPInvokeError();
            copy.Dispose();
            throw new Win32Exception(error);
        }

        return copy;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetProcessId(SafeHandle process);
}
