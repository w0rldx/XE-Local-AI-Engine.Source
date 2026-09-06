namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The <c>Pause</c> lane, and the one lane that drives nothing: parking a node run on a human is two status writes,
///     so there is no work to hold, no slot to wait for and no answer to poll for.
///     <para>
///         It is a lane rather than an arm of <see cref="GraphWorkflowInlineExecutor" /> because it does not settle:
///         every inline kind reaches a terminal inside the tick that dispatched it, and a pause deliberately does not.
///         The answer arrives through <c>IGraphWorkflowRunService.DecideAsync</c>, hours later and from a person, which
///         is the whole distinction the two classes encode.
///     </para>
///     <para>
///         The prompt, the allowed answers and <c>requireComment</c> are NOT copied onto the row: they are already in
///         the run's pinned graph, and a second copy is a second thing that can drift from the document the run
///         actually routes on. <c>PendingDecisionKind</c> names the pending ACT, singular; which answers the API will
///         accept comes from the node's <c>config.allowedDecisions</c>.
///     </para>
/// </summary>
internal sealed class GraphWorkflowPauseExecutor : IGraphWorkflowNodeExecutor
{
    public bool Owns(GraphWorkflowNodeKind kind) =>
        kind == GraphWorkflowNodeKind.Pause;

    /// <summary>
    ///     Never. A parked pause is durable state on a row, not work this process is driving — which is exactly why a
    ///     restart leaves it alone and why the cancel drain settles it here rather than asking it to stop.
    /// </summary>
    public bool IsInFlight(Guid nodeRunId) =>
        false;

    /// <summary>
    ///     Parks the node run on a human: <c>Running</c>, then <c>WaitingForApproval</c> with the pending act named.
    ///     <para>
    ///         Two writes rather than one, like every inline kind, so the <c>node.started</c> that precedes the
    ///         <c>gate.requested</c> gives a reader the moment the pause was REACHED as well as the moment it began
    ///         waiting. No output document is written: a pause's output is its answer, and it has none yet.
    ///     </para>
    /// </summary>
    public async Task<int> DispatchAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(nodeRun);

        GraphWorkflowStateMachine.EnsureLegal(nodeRun.Status, GraphWorkflowNodeRunStatus.Running, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.Running),
                           cancellationToken)
                       .ConfigureAwait(false);

        GraphWorkflowStateMachine.EnsureLegal(GraphWorkflowNodeRunStatus.Running, GraphWorkflowNodeRunStatus.WaitingForApproval, nodeRun.NodeKey);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                               nodeRun.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowNodeRunStatus.WaitingForApproval,
                               PendingDecisionKind: GraphWorkflowDecisionKind.Approve),
                           cancellationToken)
                       .ConfigureAwait(false);
        return 2;
    }

    /// <summary>
    ///     Nothing to settle, ever. Answering zero is what hands the row to the tick's deadline check — and a
    ///     <c>WaitingForApproval</c> row is not in the polled set at all, so this is reached only by a race the next
    ///     tick re-reads its way out of.
    /// </summary>
    public Task<int> PollAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken) =>
        Task.FromResult(0);

    /// <summary>
    ///     Never asked anything, so it never answers that it did: the drain reads this as "nothing in flight" and
    ///     settles the waiting row to <c>Cancelled</c> itself, which is the correct end for an unanswered pause.
    /// </summary>
    public Task<bool> StopAsync(Guid nodeRunId) =>
        Task.FromResult(false);

    public Task DiscardAsync(Guid nodeRunId) =>
        Task.CompletedTask;

    public Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns) =>
        Task.CompletedTask;
}
