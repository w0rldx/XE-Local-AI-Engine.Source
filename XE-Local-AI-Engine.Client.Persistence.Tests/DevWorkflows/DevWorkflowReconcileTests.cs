namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class DevWorkflowReconcileTests
{
    /// <summary>
    ///     T-11: only node-runs the host left mid-flight collapse. Runs auto-resume, so no run status moves — and a
    ///     durable human-wait state is not something a restart invalidates.
    /// </summary>
    [Test]
    public async Task Reconcile_CollapsesOnlyTheMidFlightNodeRunsAndLeavesTheRunAlone()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var queuedId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var waitingId = Guid.NewGuid();
        var doneId = Guid.NewGuid();
        var materialized = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand
        {
            RunId = seed.RunId,
            ExpectedVersion = seed.RunVersion,
            OperationId = Guid.NewGuid(),
            NodeRuns = [
                                              new DevWorkflowNodeRunSeed { NodeRunId = queuedId, NodeKey = "queued", NodeType = DevWorkflowNodeType.Agent },
                                              new DevWorkflowNodeRunSeed { NodeRunId = runningId, NodeKey = "running", NodeType = DevWorkflowNodeType.Tool },
                                              new DevWorkflowNodeRunSeed { NodeRunId = waitingId, NodeKey = "approval", NodeType = DevWorkflowNodeType.HumanGate },
                                              new DevWorkflowNodeRunSeed { NodeRunId = doneId, NodeKey = "done", NodeType = DevWorkflowNodeType.Agent }
                                          ]
        });

        var sessionId = Guid.NewGuid();
        var version = materialized.Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = seed.RunId,
            NodeRunId = queuedId,
            ExpectedVersion = version,
            TargetStatus = DevWorkflowNodeRunStatus.Queued,
            QueueReason = "awaiting-agent-slot"
        })).Version;
        version = (await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand { RunId = seed.RunId, NodeRunId = runningId, ExpectedVersion = version, WorkSessionId = sessionId })).Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand { RunId = seed.RunId, NodeRunId = runningId, ExpectedVersion = version, TargetStatus = DevWorkflowNodeRunStatus.Running })).Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = seed.RunId,
            NodeRunId = waitingId,
            ExpectedVersion = version,
            TargetStatus = DevWorkflowNodeRunStatus.WaitingForApproval,
            PendingDecisionKind = DevWorkflowDecisionKind.Approve
        })).Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand { RunId = seed.RunId, NodeRunId = doneId, ExpectedVersion = version, TargetStatus = DevWorkflowNodeRunStatus.Succeeded })).Version;
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand { RunId = seed.RunId, ExpectedVersion = version, TargetStatus = DevWorkflowRunStatus.Running });

        var interrupted = await store.ListInterruptedNodeRunsAsync();
        AssertEx.Equal(expected: 2, interrupted.Count, "The read answers the same rows the collapse takes, so a caller can judge them before anything is written.");

        // Judged with nothing to repair, which is what an agent resuming its own session and a never-dispatched queued
        // row both come to: the verdict still has to be there, because a row nobody judged is a row nobody collapses.
        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("The engine restarted while this node was in flight.",
                                        [.. interrupted.Select(row => Verdict(row))]);

        AssertEx.Equal(expected: 2, reconciled.Count, "Only the queued and running node runs lost an executor.");
        var reconciledRunning = reconciled.Single(row => row.NodeRunId == runningId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, reconciledRunning.Status, "The pre-collapse status is what tells the runtime what the node was doing.");
        AssertEx.Equal(DevWorkflowNodeType.Tool, reconciledRunning.NodeType);
        AssertEx.Equal(sessionId, reconciledRunning.WorkSessionId, "The session id travels with the row so the runtime needs no follow-up read per node.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Queued, reconciled.Single(row => row.NodeRunId == queuedId).Status);

        var nodeRuns = (await store.ListNodeRunsAsync(seed.RunId)).ToDictionary(nodeRun => nodeRun.Id);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRuns[queuedId].Status, "A collapsed node run goes back to Pending so the dispatcher can re-admit it.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRuns[runningId].Status);
        AssertEx.Null(nodeRuns[runningId].StartedAtUtc, "A node run about to be re-dispatched must not claim it started before the restart.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, nodeRuns[waitingId].Status, "A gate waiting on a human survives a restart untouched.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRuns[doneId].Status);

        var run = await store.GetRunAsync(seed.RunId);
        AssertEx.Equal(DevWorkflowRunStatus.Running, run.Status, "Runs auto-resume, so reconciliation never moves a run's status.");

        AssertEx.Null(nodeRuns[runningId].TerminalReason, "A row sitting at Pending must not carry a terminal reason, or the UI reads the restart as this attempt's outcome.");
        AssertEx.Null(nodeRuns[runningId].FailureClass);

        var events = await store.ListEventsAsync(seed.RunId);
        AssertEx.Equal(expected: 2, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted), "One interrupted event per collapsed node run.");
        AssertEx.True(events.Any(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted
                                         && item.DetailJson is not null
                                         && item.DetailJson.Contains("restarted", StringComparison.Ordinal)),
            "The reason moved to the event, so it must actually be readable there.");

        var second = await store.ReconcileNonTerminalNodeRunsAsync("Second pass.", []);
        AssertEx.Empty(second, "Reconciliation is idempotent by construction: a second pass finds none of those states.");
    }

    /// <summary>
    ///     The repairs a restart decides on ride along in the collapse's own transaction, because a collapse that
    ///     committed alone leaves rows the next boot reads as ordinary <c>Pending</c> and never repairs.
    /// </summary>
    [Test]
    public async Task Reconcile_AppliesItsRepairsInsideTheTransactionThatCollapsesTheRows()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var (seed, nodeRunId) = await SeedRunningToolAsync(store);

        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                                        [await ReattemptVerdictAsync(store, nodeRunId, "interrupted")]);

        AssertEx.Equal(expected: 1, reconciled.Count);
        var nodeRun = await store.GetNodeRunAsync(nodeRunId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRun.Status);
        AssertEx.Equal(expected: 2, nodeRun.Attempt, "The repair is what spends the attempt, and it committed with the collapse.");

        var events = await store.ListEventsAsync(seed.RunId);
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted));
        AssertEx.Equal(expected: 1, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeRetryScheduled), "The repair records its own event, as the separate write did.");
    }

    /// <summary>
    ///     The window the atomicity exists for: a recovery that cannot finish must leave the stranded rows exactly as
    ///     the dead host left them, so the next boot judges the same evidence rather than finding half-repaired rows.
    ///     The transaction is failed through a stale version rather than by killing a process — what is under test is
    ///     the boundary, not the cause.
    /// </summary>
    [Test]
    public async Task AReconcileThatCannotFinish_LeavesEveryStrandedNodeRunWhereTheCrashLeftIt()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var (seed, nodeRunId) = await SeedRunningToolAsync(store);

        var doomed = new DevWorkflowNodeRunVerdict
        {
            NodeRunId = nodeRunId,
            ObservedStatus = DevWorkflowNodeRunStatus.Running,
            ObservedAttempt = 1,
            ObservedWorkSessionId = null,
            Repairs = [
                new TransitionDevWorkflowNodeRunCommand { RunId = seed.RunId, NodeRunId = nodeRunId, ExpectedVersion = long.MaxValue, TargetStatus = DevWorkflowNodeRunStatus.Pending, IncrementAttempt = true }
            ]
        };
        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => store.ReconcileNonTerminalNodeRunsAsync("The host restarted.", [doomed]));

        var stranded = await store.GetNodeRunAsync(nodeRunId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, stranded.Status, "Nothing committed, so the row is still what the next boot has to reconcile.");
        AssertEx.Equal(expected: 1, stranded.Attempt);
        AssertEx.Empty((await store.ListEventsAsync(seed.RunId)).Where(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted));

        // And the next boot finds it, repairs it, and spends exactly one attempt on the one interruption.
        AssertEx.Equal(expected: 1, (await store.ListInterruptedNodeRunsAsync()).Count);
        _ = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                           [await ReattemptVerdictAsync(store, nodeRunId)]);

        var repaired = await store.GetNodeRunAsync(nodeRunId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, repaired.Status);
        AssertEx.Equal(expected: 2, repaired.Attempt);
    }

    /// <summary>
    ///     A verdict is only true of the row it was decided from. A row that moved between the caller's read and the
    ///     collapse — another process took it, or it became stranded after the read — must be left alone rather than
    ///     collapsed on evidence that no longer holds: once at <c>Pending</c> nothing would ever judge it again, and its
    ///     re-run would cost neither an attempt nor a glance at the budget.
    /// </summary>
    [Test]
    public async Task AVerdictWhoseRowMovedSinceItWasJudged_LeavesThatRowForTheNextPass()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var (seed, steadyId) = await SeedRunningToolAsync(store);
        var driftingId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, driftingId, "drifting", DevWorkflowVersions.Any, DevWorkflowNodeType.Tool);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand { RunId = seed.RunId, NodeRunId = driftingId, ExpectedVersion = version, TargetStatus = DevWorkflowNodeRunStatus.Running });

        var snapshot = await store.ListInterruptedNodeRunsAsync();
        AssertEx.Equal(expected: 2, snapshot.Count);

        // The interleaving: after the snapshot, something else re-attempts one of the two rows. It is still Running, so
        // it is still stranded — but it is no longer the row the verdict was decided from.
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = seed.RunId,
            NodeRunId = driftingId,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Running,
            IncrementAttempt = true
        });

        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                                        [.. snapshot.Select(row => Verdict(row, Reattempt(row)))]);

        AssertEx.Equal(expected: 1, reconciled.Count, "Only the row that was still what the verdict described is collapsed.");
        AssertEx.Equal(steadyId, reconciled.Single().NodeRunId);
        AssertEx.Equal(expected: 2, (await store.GetNodeRunAsync(steadyId)).Attempt);

        var drifted = await store.GetNodeRunAsync(driftingId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, drifted.Status, "The row that moved is untouched: not collapsed, and not repaired from stale evidence.");
        AssertEx.Equal(expected: 2, drifted.Attempt, "The stale repair must not have spent a second attempt on it.");

        // The pass that follows reads it as it now is and finishes the job.
        var second = await store.ListInterruptedNodeRunsAsync();
        AssertEx.Equal(expected: 1, second.Count);
        _ = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.", [.. second.Select(row => Verdict(row, Reattempt(row)))]);

        var repaired = await store.GetNodeRunAsync(driftingId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, repaired.Status);
        AssertEx.Equal(expected: 3, repaired.Attempt);
    }

    /// <summary>
    ///     The last pass has to end the matter. Nothing downstream re-reads a stranded row — the dispatcher admits
    ///     <c>Pending</c> rows and follows <c>Running</c> agent ones — so a Tool row left behind by a recovery that gave
    ///     up would wedge its run for good. A settling pass blocks what it could not judge, off the live row, and leaves
    ///     nothing stranded whatever raced it.
    /// </summary>
    [Test]
    public async Task ASettlingPass_BlocksTheStrandedRowsItCouldNotJudgeInsteadOfLeavingThemLive()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var (seed, steadyId) = await SeedRunningToolAsync(store);
        var driftingId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, driftingId, "drifting", DevWorkflowVersions.Any, DevWorkflowNodeType.Tool);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand { RunId = seed.RunId, NodeRunId = driftingId, ExpectedVersion = version, TargetStatus = DevWorkflowNodeRunStatus.Running });

        var snapshot = await store.ListInterruptedNodeRunsAsync();
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand
        {
            RunId = seed.RunId,
            NodeRunId = driftingId,
            ExpectedVersion = DevWorkflowVersions.Any,
            TargetStatus = DevWorkflowNodeRunStatus.Running,
            IncrementAttempt = true
        });

        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                                        [.. snapshot.Select(row => Verdict(row, Reattempt(row)))],
                                        new DevWorkflowUnjudgedNodeRunBlock { FailureClass = "Interrupted", SanitizedReason = "Startup recovery could not settle this node run." });

        AssertEx.Equal(expected: 2, reconciled.Count, "A settling pass takes every stranded row: the ones it judged, and the ones it could not.");
        AssertEx.Empty(await store.ListInterruptedNodeRunsAsync(), "Nothing may still be in flight when the dispatcher starts.");

        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, (await store.GetNodeRunAsync(steadyId)).Status);

        var settled = await store.GetNodeRunAsync(driftingId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, settled.Status, "A row nobody could judge goes to a human rather than staying live with no executor.");
        AssertEx.Equal(DevWorkflowDecisionKind.Abandon, settled.PendingDecisionKind, "and it asks for an answer, or no one would ever see it.");
        AssertEx.Equal(expected: 2, settled.Attempt, "Blocking costs nothing: the row never re-ran.");
        AssertEx.True(AssertEx.NotNull(settled.TerminalReason).Contains("could not settle", StringComparison.Ordinal),
            "The row has to say why a person is looking at it.");
    }

    /// <summary>The verdict a startup recovery composes for a stranded sandbox row: re-attempt it, bound to the row it read.</summary>
    private static async Task<DevWorkflowNodeRunVerdict> ReattemptVerdictAsync(DevWorkflowStore store, Guid nodeRunId, string? outcome = null)
    {
        var row = (await store.ListInterruptedNodeRunsAsync()).Single(candidate => candidate.NodeRunId == nodeRunId);
        return Verdict(row, Reattempt(row, outcome));
    }

    private static DevWorkflowNodeRunVerdict Verdict(DevWorkflowReconciledNodeRun row, params TransitionDevWorkflowNodeRunCommand[] repairs) =>
        new() { NodeRunId = row.NodeRunId, ObservedStatus = row.Status, ObservedAttempt = row.Attempt, ObservedWorkSessionId = row.WorkSessionId, Repairs = repairs };

    private static TransitionDevWorkflowNodeRunCommand Reattempt(DevWorkflowReconciledNodeRun row, string? outcome = null) =>
        new() { RunId = row.RunId, NodeRunId = row.NodeRunId, ExpectedVersion = DevWorkflowVersions.Any, TargetStatus = DevWorkflowNodeRunStatus.Pending, IncrementAttempt = true, Outcome = outcome };

    /// <summary>A run with one Tool node run the host left mid-command — the row every atomicity test starts from.</summary>
    private static async Task<(DevWorkflowSeed Seed, Guid NodeRunId)> SeedRunningToolAsync(DevWorkflowStore store)
    {
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);
        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "validate", seed.RunVersion, DevWorkflowNodeType.Tool);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand { RunId = seed.RunId, NodeRunId = nodeRunId, ExpectedVersion = version, TargetStatus = DevWorkflowNodeRunStatus.Running });
        return (seed, nodeRunId);
    }

    /// <summary>
    ///     T-12: a purged conversation takes its work session's whole subtree with it, so the node-run's pointer can
    ///     outlive its target. That has to read back as "transcript no longer available", not as an error.
    /// </summary>
    [Test]
    public async Task ANodeRunWhoseWorkSessionIsGone_ReadsBackAsUnavailableRatherThanThrowing()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = DevWorkflowTestFixture.StoreFor(context);
        var sessionStore = new AgentWorkSessionStore(context, TimeProvider.System);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store);

        var sessionId = Guid.NewGuid();
        _ = await sessionStore.CreateAsync(new CreateWorkSessionCommand
        {
            SessionId = sessionId,
            ConversationId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            Kind = AgentWorkSessionKind.Workflow,
            Title = "Research the thing",
            Objective = "Find out what we are building."
        });

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion);
        _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand { RunId = seed.RunId, NodeRunId = nodeRunId, ExpectedVersion = version, WorkSessionId = sessionId });

        AssertEx.True((await store.GetNodeRunAsync(nodeRunId)).WorkSessionAvailable);

        _ = await sessionStore.DeleteAsync(sessionId);

        var afterPurge = await store.GetNodeRunAsync(nodeRunId);
        AssertEx.Equal(sessionId, afterPurge.WorkSessionId, "The pointer stays: it is the record that a session once existed.");
        AssertEx.False(afterPurge.WorkSessionAvailable, "A purged session must read back as recoverable state rather than fail the read.");
        AssertEx.False((await store.ListNodeRunsAsync(seed.RunId)).Single().WorkSessionAvailable);
    }

    /// <summary>The Workflow session kind is admitted at both layers — the guards deny only the reserved Development kind.</summary>
    [Test]
    public async Task TheWorkSessionStore_AdmitsTheWorkflowKind()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var sessionStore = new AgentWorkSessionStore(context, TimeProvider.System);

        var created = await sessionStore.CreateAsync(new CreateWorkSessionCommand
        {
            SessionId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            Kind = AgentWorkSessionKind.Workflow,
            Title = "Workflow node",
            Objective = "Do the node's work."
        });
        AssertEx.Equal(AgentWorkSessionKind.Workflow, created.Kind);

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => sessionStore.CreateAsync(new CreateWorkSessionCommand
        {
            SessionId = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            Kind = AgentWorkSessionKind.Development,
            Title = "Reserved",
            Objective = "Reserved."
        }),
                              "Development stays reserved by the series this module supersedes.");
    }
}
