namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Testability seam for the startup orphan reaper (<c>StaleImageServerReaper</c>): enumerates the host's candidate
///     <c>sd-server</c> processes and tree-kills one by pid. The production implementation
///     (<c>OsStaleImageServerProcessScanner</c>) reads the real OS process table; unit tests substitute an in-memory fake
///     so the reaper's matching/kill logic runs with no real process. Mirrors <c>IStaleLlamaServerProcessScanner</c>.
/// </summary>
internal interface IStaleImageServerProcessScanner
{
    /// <summary>
    ///     Enumerates every running process whose name is <c>sd-server</c>, each paired with its resolved executable
    ///     path (<see langword="null" /> when the path could not be read). Best-effort: a process that exits or denies
    ///     access mid-enumeration is skipped — this never throws.
    /// </summary>
    IReadOnlyList<StaleImageServerProcess> EnumerateImageServerProcesses();

    /// <summary>
    ///     Tree-kills the process tree rooted at <paramref name="pid" />. Best-effort: an already-exited or
    ///     access-denied pid is swallowed (the caller logs the attempt) — this never throws, so a single failure never
    ///     stops the reaper from processing the remaining candidates.
    /// </summary>
    void KillProcessTree(int pid);
}

/// <summary>A candidate <c>sd-server</c> process: its OS pid and resolved executable path (<see langword="null" /> when unresolved).</summary>
internal readonly record struct StaleImageServerProcess(int Pid, string? ExecutablePath);
