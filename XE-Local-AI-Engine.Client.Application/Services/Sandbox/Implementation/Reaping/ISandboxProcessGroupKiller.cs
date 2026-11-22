namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The OS seam sandbox process groups are killed through: by the orphan reaper for a marker a dead host left (FOREIGN), and by the
///     provider for a child this host started (OWNED), so the decision logic of both is testable against a fake.
/// </summary>
public interface ISandboxProcessGroupKiller
{
    /// <summary>
    ///     Reads the start time, in clock ticks since boot, of the process with this pid, or <see langword="null" /> when it is absent OR
    ///     unreadable.
    /// </summary>
    /// <remarks>For recording a marker only: <see langword="null" /> is never proof that the process is gone.</remarks>
    long? GetProcessStartTicks(int processId);

    /// <summary><see langword="true" /> when a process with this pid currently exists (used for the owning-worker liveness check).</summary>
    bool IsProcessAlive(int processId);

    /// <summary>Kills the group a dead host's marker names, only while its leader still carries <paramref name="expectedStartTicks" />.</summary>
    /// <remarks>
    ///     SIGTERM, a grace, then SIGKILL for what is left. Fails closed: an operational failure or an unreadable identity is
    ///     <see cref="SandboxGroupKillOutcome.Deferred" />, and an absent leader is not an empty group.
    /// </remarks>
    Task<SandboxGroupKillOutcome> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default);

    /// <summary>Opens a pidfd on a child this host just started; the caller disposes it whatever its status.</summary>
    LinuxPidFd PinProcess(int processId);

    /// <summary>Calls <paramref name="killTree" /> exactly once, while the leader still parents its descendants, then SIGKILLs the group of a child this host started.</summary>
    /// <param name="leaderPin">The pin taken at launch, or <see langword="null" /> when none is usable.</param>
    /// <param name="pinStatus">How the pin's open ended; a dropped pin reads <see cref="LinuxPidFdOpenStatus.Gone" />.</param>
    /// <param name="processGroupId">The leader's pid, which <c>setsid</c> made its group id.</param>
    /// <param name="leaderExited">Whether this host has seen the leader exit; guards the numeric fallback.</param>
    /// <param name="killTree">The leader's own tree kill; false (a descendant that left the group may survive) makes the outcome Deferred.</param>
    SandboxGroupKillOutcome KillOwnedProcessGroup(LinuxPidFd? leaderPin,
        LinuxPidFdOpenStatus pinStatus,
        int processGroupId,
        Func<bool> leaderExited,
        Func<bool> killTree);
}
