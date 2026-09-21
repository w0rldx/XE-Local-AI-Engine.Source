namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Testability seam for the startup orphan reaper (<c>StaleLlamaServerReaper</c>): enumerates the host's candidate
///     <c>llama-server</c> processes and tree-kills one by pid.
/// </summary>
/// <remarks>
///     <c>OsStaleLlamaServerProcessScanner</c> reads the real OS process table; unit tests substitute an in-memory
///     fake so the reaper's matching and kill logic runs with no real process.
/// </remarks>
internal interface IStaleLlamaServerProcessScanner
{
    /// <summary>
    ///     Enumerates every running process whose name is <c>llama-server</c>, each paired with its resolved executable
    ///     path, or <see langword="null" /> when the path could not be read.
    /// </summary>
    /// <remarks>Best-effort: a process that exits or denies access mid-enumeration is skipped, and this never throws.</remarks>
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
