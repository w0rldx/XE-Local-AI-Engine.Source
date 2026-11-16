namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Windows process handle that contains the child, and any process it spawns, in a Job Object configured with
///     <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// </summary>
/// <remarks>
///     Closing the job handle — on <see cref="TreeKill" /> or <see cref="Dispose" /> — terminates the entire tree, so
///     no orphan survives a supervisor stop or crash. Every native call is reached only on Windows, the launcher
///     guarding with <see cref="OperatingSystem.IsWindows" />. <strong>Operator-verification flag:</strong> a
///     Linux build cannot exercise the Win32 path; <c>WindowsJobObjectTreeKillTests</c> proves it on a Windows host, and
///     real tree-kill behavior MUST be verified on Windows 11.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsJobObjectProcessHandle : IProcessTreeHandle
{
    private readonly SafeJobHandle _job;
    private readonly Process _process;
    private readonly ProcessStderrTail? _stderrTail;
    private int _disposed;

    private WindowsJobObjectProcessHandle(Process process, SafeJobHandle job, ProcessStderrTail? stderrTail)
    {
        _process = process;
        _job = job;
        _stderrTail = stderrTail;
    }

    public int ProcessId => _process.Id;

    public bool HasExited => ProcessExitState.SafeHasExited(_process);

    public int? ExitCode => ProcessExitState.SafeExitCode(_process);

    public string? StderrTail => _stderrTail?.Snapshot();

    public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) =>
        ProcessExitState.WaitForExitAsync(_process, timeout, ct);

    public void TreeKill()
    {
        // Closing the kill-on-close job terminates the whole tree. Idempotent: SafeHandle guards double-close.
        if (!_job.IsClosed && !_job.IsInvalid)
        {
            _job.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        try
        {
            TreeKill();
        }
        finally
        {
            _process.Dispose();
        }
    }

    /// <summary>
    ///     Creates a kill-on-close job, assigns the already-started <paramref name="process" /> to it and returns the
    ///     handle. On any failure the job and process are torn down and <paramref name="containmentFailure" /> is thrown.
    /// </summary>
    /// <remarks>The factory keeps each provider's own sanitized exception type and message.</remarks>
    public static WindowsJobObjectProcessHandle Wrap(Process process,
        Func<Exception, Exception> containmentFailure,
        ProcessStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(containmentFailure);

        SafeJobHandle? job = null;
        try
        {
            job = WindowsJobNative.CreateJobObjectW(IntPtr.Zero, lpName: null);
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            ConfigureKillOnClose(job);
            AssignProcess(job, process);
            return new WindowsJobObjectProcessHandle(process, job, stderrTail);
        }
        catch (Exception ex)
        {
            job?.Dispose();
            TryKill(process);
            process.Dispose();
            throw containmentFailure(ex);
        }
    }

    /// <summary>
    ///     Starts a process through <paramref name="start" /> and contains it as <see cref="Wrap" /> does. A start
    ///     failure propagates unchanged; only a containment failure goes through <paramref name="containmentFailure" />.
    /// </summary>
    public static WindowsJobObjectProcessHandle Start(Func<Process> start,
        Func<Exception, Exception> containmentFailure,
        ProcessStderrTail? stderrTail = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        return Wrap(start(), containmentFailure, stderrTail);
    }

    private static void ConfigureKillOnClose(SafeJobHandle job)
    {
        WindowsJobNative.SetInformation(job,
            WindowsJobNative.JobObjectExtendedLimitInformation,
            new WindowsJobNative.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new WindowsJobNative.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = WindowsJobLimits.LimitKillOnJobClose
                }
            });
    }

    private static void AssignProcess(SafeJobHandle job, Process process)
    {
        if (!WindowsJobNative.AssignProcessToJobObject(job, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!ProcessExitState.SafeHasExited(process))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup on the failure path; the sanitized exception already carries the cause.
        }
    }
}
