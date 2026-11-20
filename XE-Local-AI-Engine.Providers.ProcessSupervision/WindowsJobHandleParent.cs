namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>The never-resumed helper every job-launched command is parented by, so no sandbox pipe end is inheritable in the engine (ADR 0025).</summary>
/// <remarks>
///     The lock serializes helper creation, replacement and pending cleanup against the launches that use the helper; the handle list, not
///     the lock, isolates launches. A process ended on a failure path is released only after a signalled wait, and no launch proceeds
///     while one is unconfirmed. The parent job is kill-on-close and held only here, so the helper and every command die with the engine.
/// </remarks>
internal sealed class WindowsJobHandleParent : IDisposable
{
    internal static readonly TimeSpan ExitConfirmTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan PendingRetryTimeout = TimeSpan.FromSeconds(2);

    // Process-wide and never disposed: process exit closes the parent job, which ends the helper.
    [SupportedOSPlatform("windows")]
    private static readonly Lazy<WindowsJobHandleParent> SharedParent = new(static () => new WindowsJobHandleParent(new WindowsJobLaunchNative()));

    private static readonly string[] StreamNames = ["stdin", "stdout", "stderr"];

    private readonly IWindowsJobLaunchNative _native;
    private readonly Lock _gate = new();
    private readonly List<PendingExit> _pending = [];
    private SafeHandle? _parentJob;
    private SafeHandle? _helper;
    private int _helperId;

    internal WindowsJobHandleParent(IWindowsJobLaunchNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    [SupportedOSPlatform("windows")]
    public static WindowsJobHandleParent Shared => SharedParent.Value;

    /// <summary>Creates the command suspended in <paramref name="job" /> with the three child ends inherited through the helper, then adopts it.</summary>
    /// <remarks>
    ///     The ends are parked in the helper, listed by their helper-local values and closed there once each, whatever happened. Until
    ///     <paramref name="adopt" /> returns, this method owns the created process: any failure ends it and confirms its exit.
    /// </remarks>
    public WindowsJobChild CreateCommand(SafeHandle job,
        WindowsJobStartInfo startInfo,
        SafeHandle stdin,
        SafeHandle stdout,
        SafeHandle stderr,
        Func<WindowsJobCreatedProcess, WindowsJobChild> adopt)
    {
        ArgumentNullException.ThrowIfNull(adopt);
        lock (_gate)
        {
            var helper = EnsureHelper();
            SafeHandle[] ends = [stdin, stdout, stderr];
            var parked = new IntPtr[3];
            var count = 0;
            string? unparkFailure = null;
            WindowsJobCreatedProcess created;
            try
            {
                try
                {
                    // Recorded the moment it exists, so a later failure closes every earlier value.
                    for (; count < ends.Length; count++)
                    {
                        parked[count] = _native.Park(helper, ends[count]);
                    }

                    created = _native.CreateCommand(job, helper, startInfo, parked);
                }
                finally
                {
                    unparkFailure = UnparkAll(helper, parked.AsSpan(0, count));
                }
            }
            catch (Exception exception)
            {
                // A helper that died or could not close a value is suspect; one alive after a plain failure (a missing executable) stays.
                var retired = unparkFailure is not null || WaitSafely(helper, TimeSpan.Zero, out _) != WindowsJobExitWait.TimedOut ? Retire() : null;
                var step = count < ends.Length ? $"park the command's {StreamNames[count]} in its helper" : "create the command";
                throw Failure(step, exception, unparkFailure is null ? retired : $"; {unparkFailure}{retired}");
            }

            if (unparkFailure is not null)
            {
                created.Thread.Dispose();
                var ended = TerminateAndConfirmLocked(created.Process, created.ProcessId, "command");
                throw new InvalidOperationException($"the handle parent could not close a parked handle in its helper: {unparkFailure}{ended}{Retire()}");
            }

            try
            {
                return adopt(created);
            }
            catch (Exception exception)
            {
                created.Thread.Dispose();
                var ended = TerminateAndConfirmLocked(created.Process, created.ProcessId, "command");
                if (ended is null)
                {
                    throw;
                }

                throw new InvalidOperationException(exception.Message + ended, exception);
            }
        }
    }

    /// <summary>Ends a command and takes its process handle; returns null once its exit is confirmed, else the reason it is held pending.</summary>
    public string? TerminateAndConfirm(SafeHandle process, int processId)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            return TerminateAndConfirmLocked(process, processId, "command");
        }
    }

    /// <summary>Closes the parent job, which ends the helper and every command it parented, and every held handle.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _helper?.Dispose();
            _helper = null;
            foreach (var entry in _pending)
            {
                entry.Process.Dispose();
            }

            _pending.Clear();
            _parentJob?.Dispose();
            _parentJob = null;
        }
    }

    private SafeHandle EnsureHelper()
    {
        ReleasePending();
        if (_helper is not null)
        {
            switch (WaitSafely(_helper, TimeSpan.Zero, out var error))
            {
                case WindowsJobExitWait.TimedOut:
                    return _helper;
                case WindowsJobExitWait.Signalled:
                    _helper.Dispose();
                    _helper = null;
                    break;
                default:
                    var pid = _helperId;
                    var retired = Retire();
                    throw new InvalidOperationException(Invariant($"the handle parent could not check its helper (pid {pid}, wait error {error}){retired}"));
            }
        }

        return CreateHelper();
    }

    private SafeHandle CreateHelper()
    {
        try
        {
            _parentJob ??= _native.CreateParentJob();
        }
        catch (Exception exception)
        {
            throw Failure("create its parent job", exception, cleanup: null);
        }

        WindowsJobCreatedProcess created;
        try
        {
            created = _native.CreateHelper(_parentJob);
        }
        catch (Exception exception)
        {
            throw Failure("create its helper", exception, cleanup: null);
        }

        created.Thread.Dispose();
        try
        {
            if (!_native.IsProcessInJob(created.Process, _parentJob))
            {
                throw new InvalidOperationException("the helper is not a member of the parent job");
            }
        }
        catch (Exception exception)
        {
            throw Failure("verify its helper", exception, TerminateAndConfirmLocked(created.Process, created.ProcessId, "helper"));
        }

        _helper = created.Process;
        _helperId = created.ProcessId;
        return _helper;
    }

    // Every entry gets one more terminate and a short wait; the first that still has not exited refuses the launch.
    private void ReleasePending()
    {
        foreach (var entry in _pending.ToList())
        {
            _ = TerminateNotingError(entry.Process);
            if (WaitSafely(entry.Process, PendingRetryTimeout, out _) != WindowsJobExitWait.Signalled)
            {
                throw new InvalidOperationException(Invariant($"a process the handle parent ended has not exited ({entry.Kind}, pid {entry.ProcessId})"));
            }

            entry.Process.Dispose();
            _pending.Remove(entry);
        }
    }

    private string? Retire()
    {
        if (_helper is null)
        {
            return null;
        }

        var helper = _helper;
        _helper = null;
        return TerminateAndConfirmLocked(helper, _helperId, "helper");
    }

    // Each recorded value is closed in the helper exactly once; a failure is reported, never retried.
    private string? UnparkAll(SafeHandle helper, ReadOnlySpan<IntPtr> parked)
    {
        string? failure = null;
        foreach (var value in parked)
        {
            try
            {
                _native.Unpark(helper, value);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                failure ??= exception.Message;
            }
        }

        return failure;
    }

    private string? TerminateAndConfirmLocked(SafeHandle process, int processId, string kind)
    {
        var terminateError = TerminateNotingError(process);
        var wait = WaitSafely(process, ExitConfirmTimeout, out var error);
        if (wait == WindowsJobExitWait.Signalled)
        {
            process.Dispose();
            return null;
        }

        _pending.Add(new PendingExit
        {
            Process = process,
            ProcessId = processId,
            Kind = kind
        });
        var why = wait == WindowsJobExitWait.TimedOut ? Invariant($"no exit within {ExitConfirmTimeout.TotalSeconds} s") : Invariant($"wait error {error}");
        return Invariant($"; the {kind} (pid {processId}) was ended but its exit not confirmed ({why}{(terminateError is null ? string.Empty : ", terminate: " + terminateError)})");
    }

    private string? TerminateNotingError(SafeHandle process)
    {
        try
        {
            _native.TerminateProcess(process);
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Noted, not thrown: the wait that follows decides whether the process is gone.
            return exception.Message;
        }
    }

    private WindowsJobExitWait WaitSafely(SafeHandle process, TimeSpan timeout, out int error)
    {
        try
        {
            return _native.Wait(process, timeout, out error);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            error = exception is Win32Exception win32 ? win32.NativeErrorCode : 0;
            return WindowsJobExitWait.Failed;
        }
    }

    private static InvalidOperationException Failure(string step, Exception cause, string? cleanup) =>
        new($"the handle parent could not {step}: {cause.Message}{cleanup}", cause);

    private static string Invariant(FormattableString text) =>
        text.ToString(CultureInfo.InvariantCulture);

    private sealed class PendingExit
    {
        public required SafeHandle Process { get; init; }

        public required int ProcessId { get; init; }

        public required string Kind { get; init; }
    }
}
