namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A lane the dispatcher hands node runs to instead of executing them inside its own tick — the seam every kind
///     whose work is a model call, a tool call or anything else that takes longer than a database write goes through.
///     <para>
///         Registered as a set rather than singly: the dispatcher asks which lane <see cref="Owns" /> a kind, so a
///         slice adding one adds a registration and nothing else. That is what keeps the tick's nine-step order fixed
///         while the set of executable kinds grows.
///     </para>
///     <para>
///         Implementations are SINGLETONS: their in-flight registry and their slot count are properties of the node and
///         outlive both a tick and a DI scope. The store they write through is the scoped one the tick hands them.
///     </para>
///     <para>
///         No implementation ships in this slice. The dispatch switch therefore has no arm for <c>Agent</c>,
///         <c>Tool</c> or <c>Pause</c>, and a node run of one of those kinds fails <c>ValidationFailed</c> — an absent
///         case rather than a placeholder.
///     </para>
/// </summary>
internal interface IGraphWorkflowNodeExecutor
{
    /// <summary>Whether this lane is the one that runs <paramref name="kind" />. Exactly one lane owns any kind.</summary>
    bool Owns(GraphWorkflowNodeKind kind);

    /// <summary>Whether this node run's work is being driven right now, or has landed and not yet been read.</summary>
    bool IsInFlight(Guid nodeRunId);

    /// <summary>
    ///     Admits an eligible node run, and answers how many transitions it wrote. Queueing is not failure: a lane with
    ///     no slot free writes the <c>Queued</c> row and returns, and the next tick asks again.
    /// </summary>
    Task<int> DispatchAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Settles the row if its work has landed, and answers how many transitions that wrote. Zero means "nothing to
    ///     say", which is what lets the dispatcher offer the row to its deadline instead.
    /// </summary>
    Task<int> PollAsync(IGraphWorkflowStore store,
        GraphWorkflowRunSnapshot run,
        GraphWorkflowGraph graph,
        GraphWorkflowGraphNode node,
        GraphWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Asks the work to stop, answering whether it actually asked. <see langword="false" /> when there is nothing in
    ///     flight or the stop was already requested — the drain reaches this every tick until a poll sees the work land,
    ///     and a lane that answered <see langword="true" /> each time would spin the drain for the whole duration.
    /// </summary>
    Task<bool> StopAsync(Guid nodeRunId);

    /// <summary>
    ///     Drops the entry outright, without waiting for the work to unwind. Used before a row is re-attempted or
    ///     expired, where the answer in hand is about a try the run has already decided to replace.
    /// </summary>
    Task DiscardAsync(Guid nodeRunId);

    /// <summary>
    ///     Drops every entry whose row has moved on, once a tick before anything is polled. Without it the registry
    ///     would claim to be driving a row a retry has already re-attempted, and settle one attempt off another's answer.
    /// </summary>
    Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns);
}
