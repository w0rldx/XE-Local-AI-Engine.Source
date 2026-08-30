namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Development;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevWorkflowNodeRunTests
{
    /// <summary>
    ///     T-14: a retry bumps the attempt in place — still one row per (run, node key) — and the history the schema no
    ///     longer holds is reconstructible from the event log, which is what makes that trade honest.
    /// </summary>
    [Test]
    public async Task RetryingANode_BumpsTheAttemptInPlaceAndLeavesTheHistoryInTheEventLog()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "implement", seed.RunVersion).ConfigureAwait(false);

        var firstSessionId = Guid.NewGuid();
        var attached = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, nodeRunId, version, firstSessionId)).ConfigureAwait(false);
        var running = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, nodeRunId, attached.Version, DevWorkflowNodeRunStatus.Running))
                                 .ConfigureAwait(false);
        var failed = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                    nodeRunId,
                                    running.Version,
                                    DevWorkflowNodeRunStatus.Failed,
                                    FailureClass: "ToolCommandFailed",
                                    TerminalReason: "build failed"))
                                .ConfigureAwait(false);

        var retried = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     failed.Version,
                                     DevWorkflowNodeRunStatus.Pending,
                                     IncrementAttempt: true))
                                 .ConfigureAwait(false);
        var secondSessionId = Guid.NewGuid();
        _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, nodeRunId, retried.Version, secondSessionId, CountsAsResume: true))
                       .ConfigureAwait(false);

        var nodeRuns = await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false);
        var nodeRun = nodeRuns.Single();
        AssertEx.Equal(expected: 1, nodeRuns.Count, "A retry must never create a second row for the same node key.");
        AssertEx.Equal(expected: 2, nodeRun.Attempt);
        AssertEx.Equal(expected: 1, nodeRun.SessionResumes);
        AssertEx.Equal(secondSessionId, nodeRun.WorkSessionId);
        AssertEx.Null(nodeRun.StartedAtUtc, "A re-attempt starts clean, or the UI shows it running since its first try.");
        AssertEx.Null(nodeRun.FailureClass, "A node run trying again must not still report the previous attempt's failure class.");
        AssertEx.Null(nodeRun.TerminalReason, "Nor its previous reason — that belongs to the node.failed event, not to a row that is about to run again.");

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryScheduled), "The retry itself must be in the log.");
        AssertEx.Equal(expected: 2,
            events.Count(item => item.EventType == DevWorkflowEventTypes.WorkSessionAttached),
            "Both attempts' sessions must be reconstructible from the log, since the row only keeps the current one.");
        AssertEx.True(events.Any(item => item.EventType == DevWorkflowEventTypes.NodeFailed && item.Outcome == "failed"));
    }

    /// <summary>
    ///     Releasing the session is what makes a re-attempt a fresh one. Without it a node run back at <c>Pending</c>
    ///     still points at the session that just ended, and nothing downstream can tell "this attempt is still holding
    ///     its session" from "this attempt is over" — the two need opposite answers.
    /// </summary>
    [Test]
    public async Task ClearingTheWorkSession_ReleasesItAndTheResumeBudgetWithIt()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);
        var attached = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, nodeRunId, version, Guid.NewGuid())).ConfigureAwait(false);
        var resumed = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId,
                                     nodeRunId,
                                     attached.Version,
                                     (await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false)).WorkSessionId!.Value,
                                     CountsAsResume: true))
                                 .ConfigureAwait(false);

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                           nodeRunId,
                           resumed.Version,
                           DevWorkflowNodeRunStatus.Pending,
                           IncrementAttempt: true,
                           ClearWorkSession: true))
                       .ConfigureAwait(false);

        var nodeRun = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Null(nodeRun.WorkSessionId);
        AssertEx.Equal(expected: 0, nodeRun.SessionResumes, "The budget bounds ONE attempt's session; carrying a spent one forward would block the next before it started.");
        AssertEx.Equal(expected: 2, nodeRun.Attempt);
    }

    /// <summary>T-15: one decision per attempt, several over a node-run's life, and a replayed operation reads its own body back.</summary>
    [Test]
    public async Task Decisions_AreOnePerAttemptAndReplayReturnsTheRecordedBody()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "approval", seed.RunVersion, DevWorkflowNodeType.HumanGate)
                                                  .ConfigureAwait(false);

        var retryOperationId = Guid.NewGuid();
        var retryDecision = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                                           Guid.NewGuid(),
                                           nodeRunId,
                                           version,
                                           retryOperationId,
                                           DevWorkflowDecisionKind.Retry,
                                           "Try once more.",
                                           DecidedBySubject: "operator-subject"))
                                       .ConfigureAwait(false);

        var alreadyDecided = await AssertEx.ThrowsAsync<DevWorkflowGateAlreadyDecidedException>(() => store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                                                   Guid.NewGuid(),
                                                   nodeRunId,
                                                   retryDecision.Version,
                                                   Guid.NewGuid(),
                                                   DevWorkflowDecisionKind.Approve)),
                                               "A second decision on the SAME attempt must be rejected.")
                                           .ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowDecisionKind.Retry,
            alreadyDecided.StandingDecision,
            "The refusal carries the decision that already stands, so the API can say what happened rather than only that the click failed.");

        var retried = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     retryDecision.Version,
                                     DevWorkflowNodeRunStatus.Pending,
                                     IncrementAttempt: true))
                                 .ConfigureAwait(false);
        _ = await store.RecordDecisionAsync(new RecordDevWorkflowDecisionCommand(seed.RunId,
                           Guid.NewGuid(),
                           nodeRunId,
                           retried.Version,
                           Guid.NewGuid(),
                           DevWorkflowDecisionKind.Approve))
                       .ConfigureAwait(false);

        var decisions = await store.ListDecisionsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, decisions.Count, "One node run legitimately accumulates a decision per attempt.");
        AssertEx.Equal(expected: 1, decisions[0].Attempt);
        AssertEx.Equal(expected: 2, decisions[1].Attempt);

        // The replay read: a repeated POST has to answer with the same BODY, and the mutation result carries no
        // decision id, subject or decided-at to answer with.
        var replayed = AssertEx.NotNull(await store.FindDecisionByOperationAsync(seed.RunId, retryOperationId).ConfigureAwait(false));
        AssertEx.Equal(DevWorkflowDecisionKind.Retry, replayed.Decision);
        AssertEx.Equal("Try once more.", replayed.Comment);
        AssertEx.Equal("operator-subject", replayed.DecidedBySubject, "Without the subject the audit can say a gate was decided but not by whom.");
        AssertEx.Null(await store.FindDecisionByOperationAsync(seed.RunId, Guid.NewGuid()).ConfigureAwait(false));
    }

    /// <summary>
    ///     The run-wide re-attempt budget is admitted where the decision is written, so a Retry that is recorded but not
    ///     yet settled still counts. Two blocked node runs answered in the same tick window — before the dispatcher has
    ///     turned either answer into an attempt — is exactly the case a check taken before recording lets through: both
    ///     read the budget as unspent, and a run whose budget is one spends two.
    /// </summary>
    [Test]
    public async Task RecordDecision_CountsAnUnsettledRetryAgainstTheRunWideBudget()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var firstNodeRunId = Guid.NewGuid();
        var secondNodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, firstNodeRunId, "implement", seed.RunVersion).ConfigureAwait(false);
        _ = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, secondNodeRunId, "validate", version).ConfigureAwait(false);

        RecordDevWorkflowDecisionCommand Retry(Guid nodeRunId, int budget) =>
            new(seed.RunId, Guid.NewGuid(), nodeRunId, DevWorkflowVersions.Any, Guid.NewGuid(), DevWorkflowDecisionKind.Retry, MaxTotalAttempts: budget);

        _ = await store.RecordDecisionAsync(Retry(firstNodeRunId, budget: 1)).ConfigureAwait(false);

        var refusal = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() => store.RecordDecisionAsync(Retry(secondNodeRunId, budget: 1)),
                                        "The first Retry has promised the run's only re-attempt, and no attempt has happened yet for a sum over Attempt to see.")
                                    .ConfigureAwait(false);
        AssertEx.True(refusal.Message.Contains("as many re-attempts as this run allows", StringComparison.Ordinal),
            "The store's refusal has to read like the endpoint's, since either can reach an operator.");
        AssertEx.Equal(expected: 1, (await store.ListDecisionsAsync(seed.RunId).ConfigureAwait(false)).Count, "Exactly one of the two Retries may be admitted.");

        // Settling the first one converts the reservation into a spent attempt rather than counting it twice: a budget
        // of two still has room for the second Retry, and a budget of one still does not.
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                           firstNodeRunId,
                           DevWorkflowVersions.Any,
                           DevWorkflowNodeRunStatus.Pending,
                           IncrementAttempt: true))
                       .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() => store.RecordDecisionAsync(Retry(secondNodeRunId, budget: 1)),
                              "The re-attempt has landed, so the budget of one is spent rather than merely promised.")
                          .ConfigureAwait(false);
        _ = await store.RecordDecisionAsync(Retry(secondNodeRunId, budget: 2)).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, (await store.ListDecisionsAsync(seed.RunId).ConfigureAwait(false)).Count, "A settled Retry must not go on reserving what it already spent.");
    }

    /// <summary>Queued and Running are distinct states with distinct timestamps, which is what makes the UI's progress honest.</summary>
    [Test]
    public async Task QueuedAndRunning_AreDistinctStatesWithTheirOwnTimestampsAndReason()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);

        var queued = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                    nodeRunId,
                                    version,
                                    DevWorkflowNodeRunStatus.Queued,
                                    QueueReason: "awaiting-agent-slot"))
                                .ConfigureAwait(false);
        var afterQueue = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal("awaiting-agent-slot", afterQueue.QueueReason);
        AssertEx.True(afterQueue.QueuedAtUtc is not null);
        AssertEx.Null(afterQueue.StartedAtUtc, "Queued is not running, and the row must not pretend otherwise.");

        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, nodeRunId, queued.Version, DevWorkflowNodeRunStatus.Running))
                       .ConfigureAwait(false);
        var afterStart = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Null(afterStart.QueueReason, "A running node run is not waiting in any queue.");
        AssertEx.True(afterStart.StartedAtUtc is not null);
    }

    /// <summary>A work session may have exactly one owning node run — the reverse lookup depends on it.</summary>
    [Test]
    public async Task AttachWorkSession_RefusesASecondOwner()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var firstNodeRunId = Guid.NewGuid();
        var secondNodeRunId = Guid.NewGuid();
        var version = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                                     seed.RunVersion,
                                     Guid.NewGuid(),
                                     [
                                         new DevWorkflowNodeRunSeed(firstNodeRunId, "research", DevWorkflowNodeType.Agent),
                                         new DevWorkflowNodeRunSeed(secondNodeRunId, "plan", DevWorkflowNodeType.Agent)
                                     ]))
                                 .ConfigureAwait(false);

        var sessionId = Guid.NewGuid();
        var attached = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, firstNodeRunId, version.Version, sessionId))
                                  .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(
                              () => store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, secondNodeRunId, attached.Version, sessionId)),
                              "One session, one owner.")
                          .ConfigureAwait(false);
    }

    /// <summary>Materializing the same node key twice is a transition error, not a raw constraint violation.</summary>
    [Test]
    public async Task Materialize_RejectsANodeKeyTheRunAlreadyCarries()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, Guid.NewGuid(), "research", seed.RunVersion).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowInvalidTransitionException>(() => store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                                  version,
                                  Guid.NewGuid(),
                                  [new DevWorkflowNodeRunSeed(Guid.NewGuid(), "research", DevWorkflowNodeType.Agent)])),
                              "The node key is the node run's identity within a run.")
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A rewritten graph and the node runs it explains land in one transaction, the revision bumps once, and the
    ///     definition row is byte-unchanged — which is what keeps re-running a definition unaffected by expansion.
    /// </summary>
    [Test]
    public async Task MaterializeWithARewrittenGraph_BumpsTheRevisionOnceAndLeavesTheDefinitionAlone()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        const string Expanded = """{"schemaVersion":1,"nodes":[{"nodeKey":"implement#1","nodeType":"DevTask"}],"edges":[]}""";
        var result = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                                    seed.RunVersion,
                                    Guid.NewGuid(),
                                    [new DevWorkflowNodeRunSeed(Guid.NewGuid(), "implement#1", DevWorkflowNodeType.DevTask, MaterializationIndex: 0)],
                                    Expanded))
                                .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.GraphRevision, "One materialization, one revision.");

        var run = await store.GetRunAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(Expanded, run.GraphJson, "The run's own pinned graph is the single source of routing truth, so it is what changes.");

        var definition = await store.GetDefinitionAsync(seed.DefinitionId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowTestFixture.SampleGraph, definition.GraphJson, "The definition must be byte-unchanged after an expansion.");
        AssertEx.Equal(expected: 1, definition.Version);

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.GraphChanged), "Exactly one graph.changed event records the rewrite.");
        AssertEx.Empty(events.Where(item => item.EventType == DevWorkflowEventTypes.NodeMaterialized),
            "A rewrite reads as graph.changed; node.materialized is the initial, graph-unchanged case.");
    }

    /// <summary>
    ///     Gate 2's <c>DevTask</c> round-trip, at the layer P1 owns. X8 gives the node run two loose refs instead of a
    ///     workspace string, so what has to hold is that they resolve: the project comes from the work item, the task id
    ///     the attempt writes reads back against a REAL <c>DevelopmentTask</c> the existing Dev Mode chain drove to
    ///     <c>AwaitingApply</c>, and a re-attempt leaves the previous attempt's task where it is — that task keeps its
    ///     own evidence, and only the next attempt's own write replaces the pointer.
    ///     <para>
    ///         The executor that creates the task from the node, and drives that chain rather than a test doing it, is
    ///         B6's; nothing above this line exists to run it yet.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ADevTaskNodeRun_CarriesItsProjectAndTheTaskTheDevModeChainDroveToAwaitingApply()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var development = new DevelopmentStore(context, TimeProvider.System);
        await SeedSelectedFolderAsync(context, fixture.DatabasePath).ConfigureAwait(false);

        var (firstTask, _) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(development).ConfigureAwait(false);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store, developmentProjectId: firstTask.ProjectId).ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store,
                                                      seed.RunId,
                                                      nodeRunId,
                                                      "implement",
                                                      seed.RunVersion,
                                                      DevWorkflowNodeType.DevTask,
                                                      developmentProjectId: firstTask.ProjectId)
                                                  .ConfigureAwait(false);

        var workItem = await store.GetWorkItemAsync(seed.WorkItemId).ConfigureAwait(false);
        AssertEx.Equal(firstTask.ProjectId, workItem.DevelopmentProjectId, "O12: the Dev Mode project is the work item's, and the node run inherits it.");

        var started = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     version,
                                     DevWorkflowNodeRunStatus.Running,
                                     DevelopmentTaskId: firstTask.TaskId))
                                 .ConfigureAwait(false);

        var running = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeType.DevTask, running.NodeType);
        AssertEx.Equal(firstTask.ProjectId, running.DevelopmentProjectId);
        AssertEx.Equal(firstTask.TaskId, running.DevelopmentTaskId);
        AssertEx.Equal(DevelopmentTaskStatus.AwaitingApply,
            (await development.GetTaskAsync(firstTask.TaskId).ConfigureAwait(false)).Status,
            "The pair is only a workspace identity if it names a task Dev Mode actually drove — a dangling id would pass every assertion above.");

        var retried = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                     nodeRunId,
                                     started.Version,
                                     DevWorkflowNodeRunStatus.Pending,
                                     IncrementAttempt: true))
                                 .ConfigureAwait(false);

        var reattempting = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, reattempting.Attempt);
        AssertEx.Equal(firstTask.TaskId,
            reattempting.DevelopmentTaskId,
            "A re-attempt does not orphan the task that holds attempt 1's evidence; the executor replaces the pointer when it has a new task to point at.");

        var (secondTask, _) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(development).ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                           nodeRunId,
                           retried.Version,
                           DevWorkflowNodeRunStatus.Running,
                           DevelopmentTaskId: secondTask.TaskId))
                       .ConfigureAwait(false);

        AssertEx.Equal(secondTask.TaskId,
            (await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false)).DevelopmentTaskId,
            "The row names the task of the attempt it is running, not the first one it ever had.");
        AssertEx.Equal(expected: 1,
            await fixture.RawCountAsync("dev_workflow_node_runs", "development_task_id", secondTask.TaskId).ConfigureAwait(false),
            "Written to the column, not held only by the change tracker.");
    }

    /// <summary>
    ///     The pointer read backwards: a Development task a workflow drives can name the run driving it, which is what
    ///     lets the Dev Mode task page say the approval lives somewhere else. A task no workflow touched answers
    ///     nothing, and that is the ordinary case the page must keep behaving as it always has.
    ///     <para>
    ///         Two runs can name one task over its life — a re-run of the same definition drives the same task — and the
    ///         LATEST answers, because the question is where the approval lives now.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TheRunDrivingADevelopmentTask_IsFoundBackThroughThePointerAndTheLatestOneAnswers()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var development = new DevelopmentStore(context, TimeProvider.System);
        await SeedSelectedFolderAsync(context, fixture.DatabasePath).ConfigureAwait(false);
        var (task, _) = await DevelopmentTestFixture.SeedTaskAwaitingApplyAsync(development).ConfigureAwait(false);

        // Two clocks a minute apart, because "latest" is the node run created last and two rows written this close
        // together would otherwise be stamped inside the same millisecond.
        var earlier = new DevWorkflowStore(context, new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1)));
        var later = new DevWorkflowStore(context, new FixedClock(DateTimeOffset.UnixEpoch.AddDays(1).AddMinutes(1)));

        var first = await DevWorkflowTestFixture.SeedRunAsync(earlier, developmentProjectId: task.ProjectId).ConfigureAwait(false);
        var firstNodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(earlier,
                                                      first.RunId,
                                                      firstNodeRunId,
                                                      "implement",
                                                      first.RunVersion,
                                                      DevWorkflowNodeType.DevTask,
                                                      developmentProjectId: task.ProjectId)
                                                  .ConfigureAwait(false);
        _ = await earlier.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(first.RunId,
                             firstNodeRunId,
                             version,
                             DevWorkflowNodeRunStatus.Running,
                             DevelopmentTaskId: task.TaskId))
                         .ConfigureAwait(false);

        AssertEx.Equal(first.RunId,
            await earlier.FindRunIdForDevelopmentTaskAsync(task.TaskId).ConfigureAwait(false),
            "A task a DevTask node run named is driven by that node run's run.");
        AssertEx.Null(await earlier.FindRunIdForDevelopmentTaskAsync(Guid.NewGuid()).ConfigureAwait(false),
            "A task no workflow drives names no run, which is every task an operator created themselves.");

        var second = await DevWorkflowTestFixture.SeedRunAsync(later, developmentProjectId: task.ProjectId).ConfigureAwait(false);
        var secondNodeRunId = Guid.NewGuid();
        var secondVersion = await DevWorkflowTestFixture.AddNodeRunAsync(later,
                                                            second.RunId,
                                                            secondNodeRunId,
                                                            "implement",
                                                            second.RunVersion,
                                                            DevWorkflowNodeType.DevTask,
                                                            developmentProjectId: task.ProjectId)
                                                        .ConfigureAwait(false);
        _ = await later.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(second.RunId,
                           secondNodeRunId,
                           secondVersion,
                           DevWorkflowNodeRunStatus.Running,
                           DevelopmentTaskId: task.TaskId))
                       .ConfigureAwait(false);

        AssertEx.Equal(second.RunId,
            await later.FindRunIdForDevelopmentTaskAsync(task.TaskId).ConfigureAwait(false),
            "The run that took the task over is the one holding its approval now.");
    }

    /// <summary>The repository row a development project points at; both seeded projects share it.</summary>
    private static async Task SeedSelectedFolderAsync(NodeChatDbContext context, string databasePath)
    {
        _ = context.Add(new NodeSelectedFolder
        {
            Id = DevelopmentTestFixture.SelectedFolderId,
            Alias = "dev-workflow-test-repository",
            HostPath = Encoding.UTF8.GetBytes(Path.GetDirectoryName(databasePath)!),
            Mode = SelectedFolderMode.Copy,
            CreatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        _ = await context.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>A clock that does not move, so two writes can be ordered by more than luck.</summary>
    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            utcNow;
    }
}
