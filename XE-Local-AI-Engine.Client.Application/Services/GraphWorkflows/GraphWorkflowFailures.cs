namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Which failures a second attempt could plausibly answer differently, and which class the failing write records.
///     <para>
///         Pure and static, and deliberately not a method on the dispatcher: the dispatcher's expire stage, the agent
///         lane and the startup reconciler all settle failures, and a second copy of this split is how two of them
///         would come to disagree about whether a node is finished.
///     </para>
/// </summary>
internal static class GraphWorkflowFailures
{
    /// <summary>
    ///     The three classes a re-attempt can change the answer to. Everything else is refused deliberately: a graph
    ///     that no longer declares a node, an over-cap document, a refused gate and a cancelled run all produce the
    ///     byte-identical answer next time, so retrying them is an infinite loop rather than resilience.
    /// </summary>
    public static bool IsRetryable(GraphWorkflowFailureClass failureClass) =>
        failureClass is GraphWorkflowFailureClass.NodeFailed or GraphWorkflowFailureClass.Timeout or GraphWorkflowFailureClass.Interrupted;

    /// <summary>
    ///     The class a failing node-run write should record: the failure itself while the node has another attempt
    ///     coming, and <c>AttemptsExhausted</c> on the attempt that uses up the node's budget.
    ///     <para>
    ///         Decided HERE, at the moment of the failing write, rather than by the retry stage afterwards, because
    ///         <c>GraphWorkflowStateMachine.IsLegal</c> has no <c>Failed → Failed</c> edge — <c>Failed → Pending</c> is
    ///         its one exit from a terminal status — so there is no legal move a later re-classification could make.
    ///         The consequence, stated rather than hidden: a node declaring <c>maxAttempts: 1</c> reports
    ///         <c>AttemptsExhausted</c> on its only attempt, because the node's budget is genuinely why nothing will
    ///         try again. What actually went wrong survives on the row's reason and on its <c>node.failed</c> event.
    ///     </para>
    ///     <para>
    ///         The RUN-wide budget is not consulted here and must not be: exhausting <c>MaxTotalAttempts</c> leaves the
    ///         plain class standing, because the node still had attempts left and the run is what ran out.
    ///     </para>
    /// </summary>
    public static GraphWorkflowFailureClass Classify(GraphWorkflowFailureClass failureClass, int attempt, int maxAttempts) =>
        IsRetryable(failureClass) && attempt >= maxAttempts ? GraphWorkflowFailureClass.AttemptsExhausted : failureClass;
}
