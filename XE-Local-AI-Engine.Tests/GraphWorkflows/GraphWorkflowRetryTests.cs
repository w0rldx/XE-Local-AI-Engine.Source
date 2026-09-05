namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Retry in place: the ONE mechanism, in the dispatcher's own stage, over rows that failed. Executors and the
///     restart reconciler write plain failures and know nothing about it, which is what these tests stand in for — a
///     failure class is put on a row through the store, exactly as the lane this build does not ship would put it there.
/// </summary>
public sealed class GraphWorkflowRetryTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     A retryable failure under both budgets goes <c>Failed → Pending</c> with the attempt incremented, in ONE
    ///     atomic write carrying a <c>node.retried</c> event — and the same tick then admits the fresh attempt, which is
    ///     what makes retry-in-place a repair rather than a status.
    /// </summary>
    [Test]
    public async Task ARetryableFailureUnderBothBudgets_GoesBackToPendingOnTheNextAttemptAndRuns()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await FailedWorkNodeAsync(harness, GraphWorkflowGraphs.InlineRetryable, GraphWorkflowFailureClass.NodeFailed).ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var work = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, work.Attempt, "the attempt is incremented in the same write that clears the failure.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, work.Status, "and the fresh attempt is admitted by the tick that scheduled it.");
        AssertEx.Equal(GraphWorkflowFailureClass.None, work.FailureClass, "a re-attempt must not report the previous try's outcome while it runs.");

        var retried = (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Single(static entry => entry.EventType == "node.retried");
        AssertEx.Contains(retried.DetailJson, "NodeFailed", message: "the row cleared the failure, so the event is the only place it survives.");
        AssertEx.Contains(retried.DetailJson, "\"reason\":\"the lane said so\"");
    }

    /// <summary>
    ///     A node that declares one attempt has none left the moment it fails, so the failing write itself records
    ///     <c>AttemptsExhausted</c> — the state machine has no <c>Failed → Failed</c> edge for a later re-classification
    ///     to travel over.
    /// </summary>
    [Test]
    public async Task ASingleAttemptNodeThatTimesOut_IsClassedAttemptsExhaustedAndNotRetried()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await RunningWorkNodeAsync(harness, GraphWorkflowGraphs.InlineSingleAttempt).ConfigureAwait(false);

        _ = await ExpireAsync(harness, runId).ConfigureAwait(false);

        var work = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, work.Status);
        AssertEx.Equal(expected: 1, work.Attempt, "nothing re-attempted it.");
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, work.FailureClass);
        AssertEx.Contains(work.Error, "did not finish within", message: "what actually happened survives on the row's reason.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.retried"));
    }

    /// <summary>Spending the node's LAST attempt is what <c>AttemptsExhausted</c> means; the attempts before it are not.</summary>
    [Test]
    public async Task TheAttemptThatSpendsTheNodesBudget_IsClassedAttemptsExhausted()
    {
        await using var harness = new GraphWorkflowHarness(Host);

        // Two attempts already spent on a three-attempt node: the next failure is the one with nowhere to go.
        var runId = await RunningWorkNodeAsync(harness, GraphWorkflowGraphs.InlineRetryable).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Pending, incrementAttempt: true).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Pending, incrementAttempt: true).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        _ = await ExpireAsync(harness, runId).ConfigureAwait(false);

        var work = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(expected: 3, work.Attempt);
        AssertEx.Equal(GraphWorkflowFailureClass.AttemptsExhausted, work.FailureClass);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, work.Status, "no attempt is left, so the failure stands.");
    }

    /// <summary>
    ///     The RUN's budget refuses the retry without re-classing the failure: the node still had attempts left, and it
    ///     is not the one that ran out.
    /// </summary>
    [Test]
    public async Task ExhaustingTheRunWideBudget_LeavesThePlainClassAndWritesNoRetry()
    {
        // A private host: the total-attempt budget is host-level configuration.
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxTotalAttempts", "1"));
        var runId = await FailedWorkNodeAsync(harness, GraphWorkflowGraphs.InlineRetryable, GraphWorkflowFailureClass.NodeFailed).ConfigureAwait(false);

        // One attempt already spent run-wide, which is the whole of this host's budget.
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Pending, incrementAttempt: true).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId,
                         "work",
                         GraphWorkflowNodeRunStatus.Failed,
                         GraphWorkflowFailureClass.NodeFailed,
                         "the lane said so again")
                     .ConfigureAwait(false);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        var work = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, work.Attempt, "the run's budget is spent, so nothing re-attempted it.");
        AssertEx.Equal(GraphWorkflowFailureClass.NodeFailed, work.FailureClass, "the class the failure carried stands: the NODE had attempts left.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.retried"));
    }

    /// <summary>
    ///     A configuration failure produces the byte-identical answer next time, so retrying it is an infinite loop
    ///     rather than resilience.
    /// </summary>
    [Test]
    public async Task AValidationFailure_IsNeverRetried()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await FailedWorkNodeAsync(harness, GraphWorkflowGraphs.InlineRetryable, GraphWorkflowFailureClass.ValidationFailed).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var work = await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false);
        AssertEx.Equal(expected: 1, work.Attempt);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, work.Status);
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.retried"));
    }

    /// <summary>An over-cap document is over-cap again on the next attempt, for the same reason.</summary>
    [Test]
    public async Task AnOverCapFailure_IsNeverRetried()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await FailedWorkNodeAsync(harness, GraphWorkflowGraphs.InlineRetryable, GraphWorkflowFailureClass.OutputTooLarge).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, (await harness.ReadNodeRunAsync(runId, "work").ConfigureAwait(false)).Attempt);
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == "node.retried"));
    }

    /// <summary>A run ticked far enough that its work node is <c>Running</c>, ready to be failed or expired.</summary>
    private static async Task<Guid> RunningWorkNodeAsync(GraphWorkflowHarness harness, string graphJson)
    {
        var runId = await harness.StartRunAsync(graphJson).ConfigureAwait(false);

        // Out of Pending, then Start — after which the work node is Pending and nothing has dispatched it yet.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        return runId;
    }

    /// <summary>The same run with its work node already failed, the way a lane this build does not ship would leave it.</summary>
    private static async Task<Guid> FailedWorkNodeAsync(GraphWorkflowHarness harness, string graphJson, GraphWorkflowFailureClass failureClass)
    {
        var runId = await RunningWorkNodeAsync(harness, graphJson).ConfigureAwait(false);
        await harness.TransitionNodeRunAsync(runId, "work", GraphWorkflowNodeRunStatus.Failed, failureClass, "the lane said so").ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     One tick from a dispatcher whose clock is an hour past the row's deadline. The deadline is re-derived from
    ///     the ROW every tick, so moving the clock is all it takes — and it is why a restart cannot leave a node run
    ///     bounded by nothing.
    /// </summary>
    private static Task<int> ExpireAsync(GraphWorkflowHarness harness, Guid runId)
    {
        var expired = harness.CreateReplacementDispatcher(clock: new GraphWorkflowFixedClock(DateTimeOffset.UtcNow.AddHours(1)));
        return expired.AdvanceOnceAsync(runId, CancellationToken.None);
    }
}
