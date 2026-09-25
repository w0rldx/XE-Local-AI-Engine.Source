namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Which failures a second attempt could plausibly answer differently, and which class the failing write records.
/// </summary>
/// <remarks>
///     Pure and static, and deliberately not a method on the dispatcher: the dispatcher's expire stage, the agent lane
///     and the startup reconciler all settle failures, and a second copy of this split is how two of them would come
///     to disagree about whether a node is finished.
/// </remarks>
internal static class GraphWorkflowFailures
{
    /// <summary>The three classes a re-attempt can change the answer to.</summary>
    /// <remarks>
    ///     Everything else is refused deliberately: a graph that no longer declares a node, an over-cap document, a
    ///     refused gate, a capacity refusal and a cancelled run all produce the byte-identical answer next time, so
    ///     retrying them is an infinite loop rather than resilience.
    /// </remarks>
    public static bool IsRetryable(GraphWorkflowFailureClass failureClass) =>
        failureClass is GraphWorkflowFailureClass.NodeFailed or GraphWorkflowFailureClass.Timeout or GraphWorkflowFailureClass.Interrupted;

    /// <summary>
    ///     The class a failing node-run write should record: the failure itself while the node has another attempt
    ///     coming, and <c>AttemptsExhausted</c> on the attempt that uses up the node's budget.
    /// </summary>
    /// <remarks>
    ///     Decided at the failing write, not by the retry stage after it: <c>GraphWorkflowStateMachine.IsLegal</c> has
    ///     no <c>Failed → Failed</c> edge — <c>Failed → Pending</c> is its one exit from a terminal status — so a later
    ///     re-classification has no legal move. A node declaring <c>maxAttempts: 1</c> therefore reports
    ///     <c>AttemptsExhausted</c> on its only attempt; what went wrong survives on the row's reason and its
    ///     <c>node.failed</c> event. The RUN-wide budget is deliberately not consulted: the run ran out, not the node.
    /// </remarks>
    public static GraphWorkflowFailureClass Classify(GraphWorkflowFailureClass failureClass, int attempt, int maxAttempts) =>
        IsRetryable(failureClass) && attempt >= maxAttempts ? GraphWorkflowFailureClass.AttemptsExhausted : failureClass;
}
