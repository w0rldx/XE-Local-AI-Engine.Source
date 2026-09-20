namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>
///     The on-disk record of one live sandboxed process group, written at launch and deleted on graceful teardown, so the NEXT start of
///     this worker can find and reap children a hard host kill orphaned.
/// </summary>
/// <remarks>
///     Nothing else survives such a crash that could identify the leftovers: the container root is a fresh GUID per provider instance and a
///     sandboxed child is an arbitrary executable, so neither can be matched after the fact. It carries no secrets — a process-group id, a
///     jail path, the owner's pid and two timestamps. A marker is PRE-REGISTERED naming the unit, generated before
///     the launch, and completed with the pid once there is one, because the startup sweep reaps every engine-owned scope no marker claims
///     and a marker written afterwards leaves a window around a LIVE command's scope.
/// </remarks>
public sealed record SandboxProcessMarker
{
    /// <summary>The sandbox this process belonged to, for log correlation.</summary>
    public required string SandboxId { get; init; }

    /// <summary>
    ///     The child's process-group id, <see langword="null" /> while the marker is PENDING and for a launch that produced no signallable
    ///     group at all.
    /// </summary>
    /// <remarks>
    ///     Valid only because the child was launched under <c>setsid</c>, which makes its pid its pgid; it stays <see langword="null" />
    ///     where the mechanism was unavailable, because <c>kill(-pid)</c> against a non-leader would signal the WORKER's own group. The
    ///     reaper refuses to signal a marker without one, so the absence is the guard rather than a value to interpret.
    /// </remarks>
    public required int? ProcessGroupId { get; init; }

    /// <summary>The group leader's start time in clock ticks since boot, field 22 of <c>/proc/[pid]/stat</c>.</summary>
    /// <remarks>
    ///     The pid-reuse guard: between the crash and the next start the kernel may have recycled <see cref="ProcessGroupId" /> onto an
    ///     unrelated process, and signalling that group would kill something that was never ours, so the reaper re-reads this field and
    ///     refuses to kill unless it matches. <see langword="null" /> exactly when <see cref="ProcessGroupId" /> is: a group id without the
    ///     guard that verifies it is not something the reaper may act on.
    /// </remarks>
    public required long? LeaderStartTicks { get; init; }

    /// <summary>The sandbox's jail directory, deleted by the reaper only when it lies under the sandbox container root.</summary>
    public required string JailPath { get; init; }

    /// <summary>
    ///     <see langword="true" /> when the jail is an engine-managed trusted host workspace that must be PRESERVED
    ///     across kill and restart. The reaper reaps the process group but must never delete such a directory.
    /// </summary>
    public bool PreserveJail { get; init; }

    /// <summary>
    ///     The pid of the worker that owns this marker. A marker whose owner is still running belongs to a live worker
    ///     (a second instance, or this one) and is left strictly alone.
    /// </summary>
    public required int OwnerProcessId { get; init; }

    /// <summary>When the marker was written, for diagnostics.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    ///     The transient systemd scope the command ran in when it ran behind a filesystem boundary, known before the launch, which is what
    ///     makes the pre-registration possible.
    /// </summary>
    /// <remarks>
    ///     For such a command this, not <see cref="ProcessGroupId" />, is the reapable handle: its processes are in their own PID
    ///     namespace, so a pid recorded from outside identifies only the outermost helper and a signal to that group reaches nothing the
    ///     workload started. Recording the unit name is what lets the next start empty the cgroup instead.
    /// </remarks>
    public string? ScopeUnitName { get; init; }
}
