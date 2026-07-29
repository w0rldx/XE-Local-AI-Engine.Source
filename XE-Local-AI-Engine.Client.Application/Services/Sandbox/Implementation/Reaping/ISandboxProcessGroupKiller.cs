namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>
///     The OS seam the orphan reaper kills through, mirroring <c>IStaleLlamaServerProcessScanner</c> so the reaper's
///     decision logic can be tested against a fake without signalling anything real.
/// </summary>
public interface ISandboxProcessGroupKiller
{
    /// <summary>
    ///     Reads the start time (clock ticks since boot) of the process with this pid, or <see langword="null" /> when
    ///     no such process exists. The reaper compares it against the value recorded at launch; a mismatch means the pid
    ///     was recycled onto an unrelated process and the group must NOT be signalled.
    /// </summary>
    long? GetProcessStartTicks(int processId);

    /// <summary><see langword="true" /> when a process with this pid currently exists (used for the owning-worker liveness check).</summary>
    bool IsProcessAlive(int processId);

    /// <summary>
    ///     Signals the entire process group — <c>SIGTERM</c>, then <c>SIGKILL</c> for anything that survives the grace
    ///     period. Asynchronous because that grace is a real wait, and this project bans blocking the calling thread on
    ///     it.
    /// </summary>
    Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default);
}
