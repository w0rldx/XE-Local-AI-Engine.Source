namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     The in-memory run-state machine for Open Canvas (Preview) runs. Singleton: owns a registry of in-flight
///     <c>RunHandle</c>s keyed by runId. Each run resolves a NODE-LOCAL <c>IChatClient</c> (privacy invariant — never a
///     shared/cloud client) and drains the .AI.Agent runner in a background task, republishing every update over the hub
///     stamped with the runId. Runs are one-shot, in-memory, never persisted (a preview/debug run is transient by design).
/// </summary>
public interface IPreviewWorkflowExecutionService
{
    /// <summary>
    ///     Validates <paramref name="graph" />, creates a run, and starts the background drain. Returns the new
    ///     <c>runId</c> on success. Throws <see cref="PreviewWorkflowValidationException" /> on an invalid graph and
    ///     <see cref="PreviewWorkflowCapReachedException" /> when the concurrent-run cap is hit (→ 409).
    ///     <paramref name="connectionId" /> ties the run to the originating hub connection so a disconnect cancels it.
    /// </summary>
    Task<Guid> StartAsync(PreviewWorkflowGraph graph, string? connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Resumes a Paused run. Returns the resulting outcome: <see cref="PreviewRunCommandOutcome.Accepted" />,
    ///     <see cref="PreviewRunCommandOutcome.NotFound" /> (unknown/expired → 404), or
    ///     <see cref="PreviewRunCommandOutcome.WrongState" /> (not Paused → 409). Idempotent against a run already
    ///     terminal.
    /// </summary>
    Task<PreviewRunCommandOutcome> ContinueAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels a run. Works while Running AND Paused (cancel must unblock a run parked on a pause). Idempotent:
    ///     cancelling an unknown/terminal run returns <see cref="PreviewRunCommandOutcome.NotFound" /> /
    ///     <see cref="PreviewRunCommandOutcome.Accepted" /> without throwing.
    /// </summary>
    Task<PreviewRunCommandOutcome> CancelAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Cancels every run owned by a hub connection (called from the hub's <c>OnDisconnectedAsync</c>).</summary>
    Task CancelRunsForConnectionAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels EVERY live run and returns how many were cancelled. The operator-facing escape hatch for runs whose
    ///     ids are no longer reachable from any open page (see <see cref="ListRuns" />), so a leaked concurrency slot
    ///     never requires a node restart to reclaim.
    /// </summary>
    Task<int> CancelAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Every run the node currently knows about: the live ones plus those whose replay log is still retained
    ///     (terminal but inside the replay window, so a client that reloads can still recover the result). Ordered
    ///     oldest-first. This is what makes a run discoverable at all — before it existed, a runId that left the
    ///     client's memory was unreachable.
    /// </summary>
    IReadOnlyList<PreviewRunSnapshot> ListRuns();

    /// <summary>
    ///     One run by id, live or retained. Returns <see langword="null" /> when the run is neither live nor retained
    ///     (unknown or evicted) so the caller can answer 404 and the client can drop a stale runId from its route.
    /// </summary>
    PreviewRunSnapshot? GetRun(Guid runId);

    /// <summary>
    ///     Returns an ordered snapshot copy of the run's buffered events with <c>Seq</c> STRICTLY GREATER than
    ///     <paramref name="afterSeq" />, so a client that subscribes AFTER events were published (or after the run
    ///     finished) can replay and catch up — SignalR does not replay to late group joiners. Pass <c>-1</c> for a
    ///     client that has seen nothing (a fresh page) to replay the whole log. Returns an empty list for an
    ///     unknown/evicted run. Each buffered payload is already stamped with its per-run <c>Seq</c>, so a client
    ///     dedupes an event delivered both via replay and live by that sequence.
    /// </summary>
    IReadOnlyList<PreviewWorkflowBufferedEvent> SnapshotBufferedEvents(Guid runId, long afterSeq);

    /// <summary>
    ///     Records that a hub connection is now watching a run. Clears the abandoned-subscriber clock, which is the
    ///     only bound a Paused run is subject to (see <see cref="PreviewWorkflowExecutionOptions.AbandonedSubscriberGrace" />).
    /// </summary>
    void AddSubscriber(Guid runId, string connectionId);

    /// <summary>Drops a hub connection from a run's watcher set; the abandoned clock starts when the last one leaves.</summary>
    void RemoveSubscriber(Guid runId, string connectionId);

    /// <summary>Drops a hub connection from EVERY run's watcher set (called from the hub's <c>OnDisconnectedAsync</c>).</summary>
    void RemoveSubscriberFromAllRuns(string connectionId);
}

/// <summary>
///     A point-in-time view of one preview run for the list/get endpoints. <paramref name="IsLive" /> distinguishes a
///     run still holding a concurrency slot from one that is merely RETAINED for replay (terminal, inside the replay
///     window). <paramref name="LastSeq" /> is the highest sequence number buffered so far, so a reattaching client can
///     ask for only the events it has not seen.
/// </summary>
public sealed record PreviewRunSnapshot(
    Guid RunId,
    PreviewRunState State,
    bool IsLive,
    long StartedAtUtc,
    long LastSeq,
    int SubscriberCount,
    string? PausedNodeId,
    string? PauseRequestId);

/// <summary>
///     One buffered preview event ready for hub replay: the SignalR method name (the event type) and the already
///     seq-stamped payload (<see cref="PreviewWorkflowNodeHubEvent" /> or <see cref="PreviewWorkflowRunHubEvent" />).
///     The hub sends <see cref="Payload" /> under <see cref="MethodName" /> to the subscribing caller.
/// </summary>
public sealed record PreviewWorkflowBufferedEvent(string MethodName, object Payload);

/// <summary>Outcome of a continue/cancel command against a run.</summary>
public enum PreviewRunCommandOutcome
{
    Accepted = 0,
    NotFound = 1,
    WrongState = 2
}

/// <summary>The lifecycle state of a single preview run.</summary>
public enum PreviewRunState
{
    Running = 0,
    Paused = 1,
    Completing = 2,
    Completed = 3,
    Cancelled = 4,
    Faulted = 5
}

/// <summary>Thrown by <see cref="IPreviewWorkflowExecutionService.StartAsync" /> when the concurrent-run cap is hit (→ 409).</summary>
public sealed class PreviewWorkflowCapReachedException(int maxConcurrentRuns)
    : Exception($"The maximum number of concurrent preview runs ({maxConcurrentRuns}) has been reached.")
{
    public int MaxConcurrentRuns { get; } = maxConcurrentRuns;
}

/// <summary>
///     Thrown by <see cref="IPreviewWorkflowExecutionService.StartAsync" /> when a graph needs more distinct node-local
///     model processes than the shared loaded-process cap allows (reject-at-start → 409).
/// </summary>
public sealed class PreviewWorkflowModelCapExceededException(int distinctModelCount, int maxLoadedProcesses)
    : Exception(
        $"The workflow uses {distinctModelCount} distinct models, exceeding the maximum of {maxLoadedProcesses} concurrently loaded model processes. Reduce the number of distinct models or raise the loaded-model cap.")
{
    public int DistinctModelCount { get; } = distinctModelCount;

    public int MaxLoadedProcesses { get; } = maxLoadedProcesses;
}
