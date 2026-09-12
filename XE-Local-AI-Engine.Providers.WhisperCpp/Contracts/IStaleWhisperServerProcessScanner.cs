namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Testability seam for the startup orphan reaper: enumerates the host's candidate <c>whisper-server</c> processes
///     and tree-kills one by pid. The production implementation reads the real OS process table; unit tests substitute
///     an in-memory fake so the reaper's matching and kill logic runs with no real process.
/// </summary>
internal interface IStaleWhisperServerProcessScanner
{
    /// <summary>
    ///     Enumerates every running process named <c>whisper-server</c>, each paired with its resolved executable path
    ///     (<see langword="null" /> when the path could not be read). Best-effort: a process that exits or denies
    ///     access mid-enumeration is skipped, and this never throws.
    /// </summary>
    IReadOnlyList<StaleWhisperServerProcess> EnumerateWhisperServerProcesses();

    /// <summary>
    ///     Tree-kills the process tree rooted at <paramref name="pid" />. Best-effort: an already-exited or
    ///     access-denied pid is swallowed so one failure never stops the reaper processing the rest.
    /// </summary>
    void KillProcessTree(int pid);
}

/// <summary>A candidate <c>whisper-server</c> process: its pid and resolved executable path.</summary>
internal readonly record struct StaleWhisperServerProcess(int Pid, string? ExecutablePath);
