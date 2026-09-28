namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Testability seam for the startup orphan reaper (<see cref="StaleProcessReaper" />): enumerates the host's
///     candidate runtime processes and tree-kills one by pid.
/// </summary>
/// <remarks>
///     <see cref="OsStaleProcessScanner" /> reads the real OS process table; unit tests substitute an in-memory fake so
///     the reaper's matching and kill logic runs with no real process.
/// </remarks>
public interface IStaleProcessScanner
{
    /// <summary>
    ///     Enumerates every running process with the scanner's process name, each paired with its resolved executable
    ///     path, or <see langword="null" /> when the path could not be read.
    /// </summary>
    /// <remarks>Best-effort: a process that exits or denies access mid-enumeration is skipped, and this never throws.</remarks>
    IReadOnlyList<StaleProcess> EnumerateProcesses();

    /// <summary>
    ///     Tree-kills the process tree rooted at <paramref name="pid" />. Best-effort: an already-exited or
    ///     access-denied pid is swallowed (the caller logs the attempt) — this never throws, so a single failure never
    ///     stops the reaper from processing the remaining candidates.
    /// </summary>
    void KillProcessTree(int pid);

    /// <summary>
    ///     The identity of <paramref name="pid" /> read from <c>/proc/[pid]/stat</c>, or <see langword="null" /> when the process
    ///     is gone, the entry is unreadable, or the platform has no <c>/proc</c>. Never throws.
    /// </summary>
    ProcessStat? ReadStat(int pid);

    /// <summary>
    ///     The executable realpath of <paramref name="pid" /> read from <c>/proc/[pid]/exe</c>, whatever the process is named, or
    ///     <see langword="null" /> when the process is gone, the link is unreadable, or the platform has no <c>/proc</c>. Never throws.
    /// </summary>
    string? ReadExecutablePath(int pid);

    /// <summary>
    ///     SIGKILLs <paramref name="pid" /> (its whole group when it leads one) ONLY while it still carries
    ///     <paramref name="expectedStartTicks" />, re-read immediately before the signal so a recycled pid is never signalled.
    ///     Returns whether the signal was sent. Never throws.
    /// </summary>
    bool KillIfSameProcess(int pid, long expectedStartTicks);
}
