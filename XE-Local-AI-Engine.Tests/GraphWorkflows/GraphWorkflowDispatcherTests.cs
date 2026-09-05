namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The tick, over the real store and a real database. Nothing is faked: the only reason a run stops short here is
///     that this build ships no <c>Tool</c> lane, so a Tool node has no executor — which is itself one of the
///     assertions. The Agent lane it does ship is exercised by <c>GraphWorkflowAgentExecutorTests</c>.
/// </summary>
public sealed class GraphWorkflowDispatcherTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     One layer per tick, and no more: a node is admitted against the rows as they were at the START of the
    ///     admission, so its successor waits for the tick after. That is what makes a fan-out's timing visible.
    /// </summary>
    [Test]
    public async Task ATick_AdvancesTheGraphByOneLayer()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear, """{"seed":1}""").ConfigureAwait(false);

        AssertEx.Equal(expected: 1, await harness.AdvanceAsync(runId).ConfigureAwait(false), "the first tick only moves the run out of Pending.");
        AssertEx.Equal(GraphWorkflowRunStatus.Running, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "start").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "middle").ConfigureAwait(false)).Status,
            "the successor waits for the tick after the one that succeeded its predecessor.");

        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "middle").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, (await harness.ReadNodeRunAsync(runId, "done").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A run that reaches a succeeded <c>End</c> completes, and the run's own result is that node's
    ///     <c>output.result</c> — written in the transition that completes it, because there is no earlier moment at
    ///     which "the run's answer" is a thing that exists.
    /// </summary>
    [Test]
    public async Task ARunThatReachesItsEnd_CompletesAndTakesTheEndNodesResult()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear, """{"seed":7}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, run.Status);
        AssertEx.Contains(run.OutputJson, "\"seed\":7", message: "the End node's resultPath projected the run input back out.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "run.created, run.started, node.started");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "run.completed");
    }

    /// <summary>
    ///     A <c>Condition</c> routes on its OWN output document, which pass-through makes the predecessor's payload.
    ///     The branch not taken dies, and every node behind it is skipped with a reason naming its cause rather than
    ///     with a bare status a reader cannot trace back.
    /// </summary>
    [Test]
    public async Task AConditionRoutes_AndTheBranchNotTakenCascadesSkippedWithANamedReason()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineBranch, """{"requiresReview":true}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "yes").ConfigureAwait(false)).Status);
        AssertEx.Contains((await harness.ReadNodeRunAsync(runId, "check").ConfigureAwait(false)).OutputJson,
            "\"branch\":\"yes\"",
            message: "the label of the edge that fired is the recorded answer to which way the run went.");

        var notTaken = await harness.ReadNodeRunAsync(runId, "no").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, notTaken.Status);
        AssertEx.Contains(notTaken.Error, "routed elsewhere", message: "the node whose condition sent the run the other way is the cause.");

        var cascaded = await harness.ReadNodeRunAsync(runId, "after").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, cascaded.Status);
        AssertEx.Contains(cascaded.Error, "'no' was skipped", message: "a cascaded skip names the skip it followed, not the condition four nodes back.");
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>A fan-out admits every branch on ONE tick — the property Parallel exists to make observable.</summary>
    [Test]
    public async Task AParallelFanOut_AdmitsBothBranchesOnOneTick()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll).ConfigureAwait(false);

        // Out of Pending, then start, then fanout — and the tick after that is the fan-out itself.
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "fast").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "slow").ConfigureAwait(false)).Status,
            "both branches of the fan-out were admitted by the same tick.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending,
            (await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).Status,
            "an All join does not proceed on the branch that happened to be shorter.");
    }

    /// <summary>
    ///     An <c>All</c> join waits while any inbound edge is still undecided, and admits once every one of them has
    ///     settled satisfied. Pending outranks everything, so the answer cannot depend on which branch landed first.
    /// </summary>
    [Test]
    public async Task AnAllJoin_WaitsForTheLongerBranchAndThenAdmits()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAll).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).Status);
        AssertEx.Contains((await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).OutputJson,
            "\"fast\"",
            message: "a join emits the per-source map, so everything downstream sees every branch.");
        AssertEx.Contains((await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).OutputJson, "\"slower\"");
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>One satisfied branch is the whole contract of an <c>Any</c> join, even with a dead sibling beside it.</summary>
    [Test]
    public async Task AnAnyJoin_AdmitsOnOneSatisfiedEdge()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAny, """{"route":"left"}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "right").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     An <c>Any</c> join with nothing that can still arrive is skipped, and a run whose every node is terminal
    ///     without a succeeded end reads <c>Cancelled</c> with a reason naming the ends it never reached — not
    ///     <c>Completed</c> like a run that did its job, and not <c>Failed</c> when nothing failed.
    /// </summary>
    [Test]
    public async Task AnAnyJoinWithNoBranchLeft_IsSkippedAndTheRunIsCancelledWithAReason()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineJoinAny, """{"route":"neither"}""").ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, (await harness.ReadNodeRunAsync(runId, "merge").ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
        AssertEx.Equal(GraphWorkflowFailureClass.None,
            (await harness.ReadRunAsync(runId).ConfigureAwait(false)).FailureClass,
            "nothing failed and no gate was refused: the run simply reached no end.");
    }

    /// <summary>
    ///     The absent case, asserted rather than assumed: this build registers no <c>Tool</c> lane, so the dispatch
    ///     switch has no arm for one and the node run says so instead of queueing behind a lane that never arrives.
    ///     A failed node run is what makes the RUN fail.
    /// </summary>
    [Test]
    public async Task ANodeKindWithNoExecutorInThisBuild_FailsValidationFailedAndFailsTheRun()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.ToolNode).ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var analyze = await harness.ReadNodeRunAsync(runId, "lookup").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, analyze.FailureClass);
        AssertEx.Contains(analyze.Error, "no executor for that kind");
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     A node run whose key the run's PINNED graph does not declare cannot be routed and must not be guessed at.
    ///     There is no <c>Blocked</c> state in v1, so it is a node failure and the recomputation turns it into a run one.
    /// </summary>
    [Test]
    public async Task ANodeRunTheGraphDoesNotDeclare_FailsValidationFailed()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);
        var runId = await harness.StartRunThroughTheStoreAsync(definitionId,
                                     GraphWorkflowGraphs.InlineLinear,
                                     [
                                         ("start", GraphWorkflowNodeKind.Start),
                                         ("middle", GraphWorkflowNodeKind.Parallel),
                                         ("done", GraphWorkflowNodeKind.End),
                                         ("phantom", GraphWorkflowNodeKind.Parallel)
                                     ])
                                 .ConfigureAwait(false);

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        var phantom = await harness.ReadNodeRunAsync(runId, "phantom").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, phantom.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, phantom.FailureClass);
        AssertEx.Contains(phantom.Error, "no longer declares node 'phantom'");
    }

    /// <summary>
    ///     A run whose pinned graph cannot be parsed is failed ONCE and written down. Rethrowing would leave the sweep
    ///     retrying an answer that cannot change, forever.
    /// </summary>
    [Test]
    public async Task ARunWhosePinnedGraphNoLongerParses_IsFailedOnceAndNotRetried()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);
        var runId = await harness.StartRunThroughTheStoreAsync(definitionId, "{ not json at all", [("start", GraphWorkflowNodeKind.Start)]).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, await harness.AdvanceAsync(runId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(runId).ConfigureAwait(false), "a terminal run is not advanced again.");

        var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowRunStatus.Failed, run.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, run.FailureClass);
    }

    /// <summary>
    ///     A productive tick re-signals, because a tick advances the graph by one layer and there is almost always more
    ///     to do; a quiescent one does not, or the loop would never stop asking about a run that has finished.
    /// </summary>
    [Test]
    public async Task AProductiveTickReSignals_AndAQuiescentOneDoesNot()
    {
        // A private host: the signal channel is the dispatcher's own, and a sibling's run draining it would decide
        // this test's answer.
        await using var harness = new GraphWorkflowHarness();
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);

        await harness.AdvanceSafelyAsync(runId).ConfigureAwait(false);
        AssertEx.True(harness.WasSignalled(runId), "the tick that started the run wrote a transition, so it asked for another.");

        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        _ = harness.WasSignalled(runId);

        await harness.AdvanceSafelyAsync(runId).ConfigureAwait(false);
        AssertEx.False(harness.WasSignalled(runId), "a tick over a finished run writes nothing and asks for nothing.");
    }

    /// <summary>
    ///     The concurrency cap holds a run <c>Pending</c> rather than refusing it: the run keeps its rows and its place,
    ///     and the next sweep offers it again.
    ///     <para>
    ///         Exercised at a cap of ONE, which is the number that catches the mistake: admission counts the runs that
    ///         are executing, never the <c>Pending</c> queue it draws from — a count that included the run asking to
    ///         start would make a cap of one admit nothing at all.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheConcurrencyCap_HoldsAFurtherRunPendingRatherThanRefusingIt()
    {
        // A private host: the cap counts executing runs across the whole database, so it cannot be asserted on a shared one.
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxConcurrentRuns", "1"));
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);
        var first = await harness.StartRunOfAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, await harness.AdvanceAsync(first).ConfigureAwait(false), "the only slot is free, so the first run takes it.");

        var second = await harness.StartRunOfAsync(definitionId).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, await harness.AdvanceAsync(second).ConfigureAwait(false), "the cap is reached, so the tick writes nothing.");
        AssertEx.Equal(GraphWorkflowRunStatus.Pending, (await harness.ReadRunAsync(second).ConfigureAwait(false)).Status);

        _ = await harness.AdvanceUntilQuiescentAsync(first).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(first).ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 1, await harness.AdvanceAsync(second).ConfigureAwait(false), "with the first run finished the queue drains on the next offer.");
    }

    /// <summary>
    ///     A backlog deeper than the cap still drains. The regression this pins is admission counting the queue: with
    ///     three <c>Pending</c> runs against a cap of one, a count that included them would answer three every time and
    ///     the node would never start anything again.
    /// </summary>
    [Test]
    public async Task ABacklogDeeperThanTheConcurrencyCap_StillDrainsRunByRun()
    {
        // A private host, for the same reason: the cap is a property of the whole database.
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxConcurrentRuns", "1"));
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear).ConfigureAwait(false);
        var runIds = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            runIds.Add(await harness.StartRunOfAsync(definitionId).ConfigureAwait(false));
        }

        // Every run is Pending before the first tick, which is the state the cap used to read as "three are running".
        foreach (var runId in runIds)
        {
            _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);
        }

        foreach (var runId in runIds)
        {
            AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status, "every queued run reaches its End.");
        }
    }

    /// <summary>
    ///     The one thing the container wiring has to get right: the signal every command path calls after its commit is
    ///     the dispatcher itself, not a second object nothing is listening to.
    /// </summary>
    [Test]
    public async Task TheDispatcherSignal_ResolvesToTheDispatcher()
    {
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
            }
        };

        AssertEx.True(ReferenceEquals(factory.Services.GetRequiredService<IGraphWorkflowDispatcherSignal>(), factory.Services.GetRequiredService<GraphWorkflowDispatcher>()),
            "one instance under both service types, or a committed command would signal nothing.");
    }
}
