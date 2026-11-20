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
    /// <remarks>The stdout and stderr streams are overlapped, so a read another process keeps blocked is cancelled through its token.</remarks>
    WindowsJobChild CreateSuspended(SafeHandle job, WindowsJobStartInfo startInfo);

    bool IsProcessInJob(SafeHandle process, SafeHandle job);

    void Resume(SafeHandle thread);

    void Terminate(SafeHandle job);

    /// <summary>
    ///     Terminates one process directly, the only kill that reaches a child its job does not hold, and takes ownership of its handle:
    ///     null once its exit is confirmed, else why it is held, and until it exits no new job launch proceeds.
    /// </summary>
    string? TerminateAndConfirm(SafeHandle process, int processId);

    /// <summary>Completes when the process has exited; owns whatever it waits on, so the caller may close its handles at any time.</summary>
    Task WaitForExitAsync(SafeHandle process);

    int GetExitCode(SafeHandle process);

    /// <summary>Commits and releases <paramref name="bytes" /> in the process, charging its job: the probe's commit control.</summary>
    bool TryCommit(SafeHandle process, ulong bytes, out int error);
}
