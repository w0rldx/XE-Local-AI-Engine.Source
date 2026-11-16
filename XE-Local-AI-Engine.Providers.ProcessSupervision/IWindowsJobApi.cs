namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>
///     The seam over every native call one job-contained launch makes, so <see cref="WindowsJobLauncher" />'s ordering and cleanup, and
///     <see cref="WindowsJobCeilingsProbe" />'s verdicts, are tested on any host. <see cref="WindowsJobApi" /> is the only real implementation.
/// </summary>
internal interface IWindowsJobApi
{
    /// <summary><c>GetActiveProcessorCount(ALL_PROCESSOR_GROUPS)</c>: the CPU-rate denominator.</summary>
    int GetSystemProcessorCount();

    SafeHandle CreateJob();

    void SetExtendedLimits(SafeHandle job, WindowsJobLimits limits);

    void SetCpuRate(SafeHandle job, uint cpuRate);

    /// <summary>Reads the extended limits and the CPU rate back; an unexpected CPU control flag set throws.</summary>
    WindowsJobLimits QueryLimits(SafeHandle job);

    /// <summary>Creates the process suspended, inside <paramref name="job" /> from creation, with three fresh stdio pipes.</summary>
    WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo);

    bool IsProcessInJob(SafeHandle process, SafeHandle job);

    void Resume(SafeHandle thread);

    void Terminate(SafeHandle job);

    /// <summary>Completes when the process has exited; owns whatever it waits on, so the caller may close its handles at any time.</summary>
    Task WaitForExitAsync(SafeHandle process);

    int GetExitCode(SafeHandle process);

    /// <summary>Cancels a read blocked on a pipe another process still holds open.</summary>
    void CancelPendingReads(Stream stream);

    /// <summary>Commits and releases <paramref name="bytes" /> in the process, charging its job: the probe's commit control.</summary>
    bool TryCommit(SafeHandle process, ulong bytes, out int error);
}
