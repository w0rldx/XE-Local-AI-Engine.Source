namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

/// <summary>Starts one command inside its own Job Object, bounded from its first instruction (ADR 0025).</summary>
/// <remarks>
///     The job is created, limited and read back; the process is created SUSPENDED inside it (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>), its
///     membership verified, and only then resumed. Any failure before the resume terminates and closes the job and every handle, so the
///     payload never runs; there is no "run unbounded" fallback. A governor, not a boundary: work started through WMI or the Task Scheduler
///     is outside the job, and a member can open the engine process (ADR 0025).
/// </remarks>
public sealed class WindowsJobLauncher
{
    private readonly IWindowsJobApi _api;

    [SupportedOSPlatform("windows")]
    public WindowsJobLauncher()
        : this(new WindowsJobApi())
    {
    }

    internal WindowsJobLauncher(IWindowsJobApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>The logical processor count of the whole system, the denominator <see cref="WindowsJobLimits.Map" /> needs.</summary>
    public int SystemProcessorCount => _api.GetSystemProcessorCount();

    /// <summary>Starts <paramref name="startInfo" /> inside a fresh job carrying exactly <paramref name="limits" />.</summary>
    /// <exception cref="Win32Exception">A native step failed; nothing was started.</exception>
    /// <exception cref="InvalidOperationException">A read-back differed or the process was not a member; nothing ran.</exception>
    public WindowsJobProcess Start(WindowsJobStartInfo startInfo, WindowsJobLimits limits)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        SafeHandle? job = null;
        WindowsJobChild? child = null;
        try
        {
            job = _api.CreateJob();
            _api.SetExtendedLimits(job, limits);
            if (limits.CpuRate > 0)
            {
                _api.SetCpuRate(job, limits.CpuRate);
            }

            // Exact, LimitFlags included: an equal flag set proves no breakaway flag and no limit the engine did not ask for.
            var readBack = _api.QueryLimits(job);
            if (readBack != limits)
            {
                throw new InvalidOperationException($"the Windows job read back {Describe(readBack)} instead of {Describe(limits)}");
            }

            child = _api.CreateSuspended(job, startInfo);
            if (!_api.IsProcessInJob(child.Process, job))
            {
                throw new InvalidOperationException("the new process is not a member of its Windows job, so it was never resumed");
            }

            if (!startInfo.RedirectStandardInput)
            {
                // Closed before the resume, so a failure here is a refusal with nothing run, never the engine's own stdin.
                child.StandardInput.Dispose();
            }

            // The exit wait is registered while the process is still suspended, so a failure there leaves nothing that ran.
            var process = new WindowsJobProcess(_api, job, child);
            _api.Resume(child.Thread);
            child.Thread.Dispose();
            return process;
        }
        catch (Exception failure)
        {
            // A child its job does not hold (the membership check failed) is out of reach of the job's terminate: kill it directly.
            var unconfirmed = child is null ? null : _api.TerminateAndConfirm(child.ReleaseProcess(), child.ProcessId);
            if (job is not null)
            {
                TryTerminate(job);
            }

            child?.Dispose();
            job?.Dispose();
            if (unconfirmed is null)
            {
                throw;
            }

            throw new InvalidOperationException(failure.Message + unconfirmed, failure);
        }
    }

    internal static string Describe(WindowsJobLimits limits) =>
        string.Create(CultureInfo.InvariantCulture,
            $"flags 0x{limits.LimitFlags:X}, commit {limits.JobMemoryLimit} bytes, {limits.ActiveProcessLimit} active processes, CPU rate {limits.CpuRate}");

    private void TryTerminate(SafeHandle job)
    {
        try
        {
            _api.Terminate(job);
        }
        catch (Win32Exception)
        {
            // Closing the kill-on-close job below still takes any member; the original failure is the one to report.
        }
    }
}
