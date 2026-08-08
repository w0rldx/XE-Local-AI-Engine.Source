namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Testability seam for the startup orphan reaper (<c>StaleLlamaServerReaper</c>): enumerates the host's candidate
///     <c>llama-server</c> processes and tree-kills one by pid. The production implementation
///     (<c>OsStaleLlamaServerProcessScanner</c>) reads the real OS process table; unit tests substitute an in-memory fake
///     so the reaper's matching/kill logic runs with no real process.
/// </summary>
internal interface IStaleLlamaServerProcessScanner
{
    /// <summary>
    ///     Enumerates every running process whose name is <c>llama-server</c>, each paired with its resolved executable
    ///     path (<see langword="null" /> when the path could not be read). Best-effort: a process that exits or denies
    ///     access mid-enumeration is skipped — this never throws.
    /// </summary>
    IReadOnlyList<StaleLlamaServerProcess> EnumerateLlamaServerProcesses();

    /// <summary>
    ///     Tree-kills the process tree rooted at <paramref name="pid" />. Best-effort: an already-exited or
    ///     access-denied pid is swallowed (the caller logs the attempt) — this never throws, so a single failure never
    ///     stops the reaper from processing the remaining candidates.
    /// </summary>
    void KillProcessTree(int pid);
}

/// <summary>A candidate <c>llama-server</c> process: its OS pid and resolved executable path (<see langword="null" /> when unresolved).</summary>
internal readonly record struct StaleLlamaServerProcess(int Pid, string? ExecutablePath);
