namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;

/// <summary>
///     The on-disk record of one live sandboxed process group, written at launch and deleted on graceful teardown. Its
///     only purpose is to let the NEXT start of this worker find and reap children that a hard host kill orphaned — the
///     provider's <c>Dispose</c> / <c>KillAsync</c> paths never run in that case, and nothing else survives the crash
///     that could identify the leftovers: the jail container root is a fresh GUID per provider instance, and a sandboxed
///     child is an arbitrary executable, so neither the path nor the process name can be matched after the fact.
///     <para>
///         It carries no secrets — a process-group id, a jail path, the owning worker's pid, and two timestamps.
///     </para>
/// </summary>
public sealed record SandboxProcessMarker
{
    /// <summary>The sandbox this process belonged to, for log correlation.</summary>
    public required string SandboxId { get; init; }

    /// <summary>
    ///     The child's process-group id. Valid only because the child was launched under <c>setsid</c>, which makes its
    ///     pid its pgid; a marker is not written when the process-group mechanism was unavailable, because
    ///     <c>kill(-pid)</c> against a non-leader would signal the WORKER's own group.
    /// </summary>
    public required int ProcessGroupId { get; init; }

    /// <summary>
    ///     The group leader's start time in clock ticks since boot (field 22 of <c>/proc/[pid]/stat</c>). This is the
    ///     pid-reuse guard: between the crash and the next start the kernel may have recycled
    ///     <see cref="ProcessGroupId" /> onto an unrelated process, and signalling that group would kill something that
    ///     was never ours. The reaper re-reads this field and refuses to kill unless it still matches.
    /// </summary>
    public required long LeaderStartTicks { get; init; }

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
    ///     The transient systemd scope the command ran in, when it ran behind a filesystem boundary; otherwise
    ///     <see langword="null" />.
    ///     <para>
    ///         For such a command this — not <see cref="ProcessGroupId" /> — is the reapable handle. Its processes are
    ///         in their own PID namespace, so a pid recorded from outside identifies only the outermost helper, and a
    ///         signal to that group reaches nothing the workload started. Recording the unit name is what lets the
    ///         next start empty the cgroup instead.
    ///     </para>
    /// </summary>
    public string? ScopeUnitName { get; init; }
}
