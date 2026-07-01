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
    ///     Returns an ordered snapshot copy of the run's buffered events so a client that subscribes AFTER events were
    ///     published (or after the run finished) can replay and catch up — SignalR does not replay to late group
    ///     joiners. Returns an empty list for an unknown/evicted run. Each buffered payload is already stamped with its
    ///     per-run <c>Seq</c>, so a client dedupes an event delivered both via replay and live by that sequence.
    /// </summary>
    IReadOnlyList<PreviewWorkflowBufferedEvent> SnapshotBufferedEvents(Guid runId);
}

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
