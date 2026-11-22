namespace XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;

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
    /// <remarks>Never on Linux: a foreign pid there goes through <see cref="KillIfSameProcess" />, which verifies it before any signal.</remarks>
    [UnsupportedOSPlatform("linux")]
    void KillProcessTree(int pid);

    /// <summary>
    ///     The identity of <paramref name="pid" /> read from <c>/proc/[pid]/stat</c>, or <see langword="null" /> when the process
    ///     is gone, the entry is unreadable, or the platform has no <c>/proc</c>. Never throws.
    /// </summary>
    ProcessStat? ReadStat(int pid);

    /// <summary>
    ///     SIGKILLs <paramref name="pid" /> (its whole group when it leads one) ONLY while it still runs <paramref name="expectedExecutablePath" />
    ///     and carries <paramref name="expectedStartTicks" />, both read after a pidfd pinned it, and is not this host's own child. Never throws.
    /// </summary>
    StaleProcessKillResult KillIfSameProcess(int pid, string expectedExecutablePath, long expectedStartTicks);

    /// <summary>The running kernel's boot id, compared against a receipt's <see cref="ProcessSpawnReceipt.BootId" />; null when it cannot be read.</summary>
    string? CurrentBootId => LinuxBootId.Current;
}

/// <summary>What <see cref="IStaleProcessScanner.KillIfSameProcess" /> did; only the first two let the reaper drop the receipt.</summary>
public enum StaleProcessKillResult
{
    /// <summary>The recorded process was signalled.</summary>
    Killed,

    /// <summary>The pid is gone or names another process: nothing was signalled, and the receipt can never match again.</summary>
    Mismatch,

    /// <summary>The pid lives but its <c>/proc</c> identity could not be read (EMFILE, EACCES): nothing was signalled.</summary>
    Unreadable,

    /// <summary>A pidfd call or the numeric group kill failed operationally (EPERM, EMFILE): nothing was signalled.</summary>
    Deferred,

    /// <summary>The pid is this host's own child: its receipt is live, so nothing was signalled and the receipt stays.</summary>
    OwnChild
}
