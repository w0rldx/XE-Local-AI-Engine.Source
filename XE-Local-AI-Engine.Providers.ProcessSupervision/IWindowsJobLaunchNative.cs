namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Runtime.InteropServices;

/// <summary>
///     The native calls <see cref="WindowsJobHandleParent" /> makes, so its ordering, rollback and pending cleanup are tested on any host.
///     <see cref="WindowsJobLaunchNative" /> is the only real implementation; every failure is a thrown exception.
/// </summary>
internal interface IWindowsJobLaunchNative
{
    /// <summary>A job whose only limit is kill-on-close, read back exactly.</summary>
    SafeHandle CreateParentJob();

    /// <summary>The handle parent: the system <c>cmd.exe</c>, suspended, inheriting nothing, created inside <paramref name="parentJob" />.</summary>
    WindowsJobCreatedProcess CreateHelper(SafeHandle parentJob);

    /// <summary>Duplicates <paramref name="end" /> into the helper as inheritable; the result is a helper-local value, never an engine handle.</summary>
    IntPtr Park(SafeHandle helper, SafeHandle end);

    /// <summary>Creates the command suspended, in <paramref name="job" />, parented by the helper, inheriting exactly <paramref name="parked" />.</summary>
    WindowsJobCreatedProcess CreateCommand(SafeHandle job, SafeHandle helper, WindowsJobStartInfo startInfo, ReadOnlySpan<IntPtr> parked);

    /// <summary>Closes one parked value inside the helper (<c>DUPLICATE_CLOSE_SOURCE</c>, which closes it whatever the result).</summary>
    void Unpark(SafeHandle helper, IntPtr parked);

    bool IsProcessInJob(SafeHandle process, SafeHandle job);

    void TerminateProcess(SafeHandle process);

    /// <summary>Waits up to <paramref name="timeout" /> for the process to exit; <paramref name="error" /> is the Win32 error of a failed wait.</summary>
    WindowsJobExitWait Wait(SafeHandle process, TimeSpan timeout, out int error);
}
