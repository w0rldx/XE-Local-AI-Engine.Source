namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

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
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var queuedId = Guid.NewGuid();
        var runningId = Guid.NewGuid();
        var waitingId = Guid.NewGuid();
        var doneId = Guid.NewGuid();
        var materialized = await store.MaterializeNodeRunsAsync(new MaterializeDevWorkflowNodesCommand(seed.RunId,
                                          seed.RunVersion,
                                          Guid.NewGuid(),
                                          [
                                              new DevWorkflowNodeRunSeed(queuedId, "queued", DevWorkflowNodeType.Agent),
                                              new DevWorkflowNodeRunSeed(runningId, "running", DevWorkflowNodeType.Tool),
                                              new DevWorkflowNodeRunSeed(waitingId, "approval", DevWorkflowNodeType.HumanGate),
                                              new DevWorkflowNodeRunSeed(doneId, "done", DevWorkflowNodeType.Agent)
                                          ]))
                                      .ConfigureAwait(false);

        var sessionId = Guid.NewGuid();
        var version = materialized.Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                  queuedId,
                                  version,
                                  DevWorkflowNodeRunStatus.Queued,
                                  QueueReason: "awaiting-agent-slot"))
                              .ConfigureAwait(false)).Version;
        version = (await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, runningId, version, sessionId)).ConfigureAwait(false)).Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, runningId, version, DevWorkflowNodeRunStatus.Running))
                              .ConfigureAwait(false)).Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                  waitingId,
                                  version,
                                  DevWorkflowNodeRunStatus.WaitingForApproval,
                                  PendingDecisionKind: DevWorkflowDecisionKind.Approve))
                              .ConfigureAwait(false)).Version;
        version = (await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, doneId, version, DevWorkflowNodeRunStatus.Succeeded))
                              .ConfigureAwait(false)).Version;
        _ = await store.TransitionRunAsync(new TransitionDevWorkflowRunCommand(seed.RunId, version, DevWorkflowRunStatus.Running)).ConfigureAwait(false);

        var interrupted = await store.ListInterruptedNodeRunsAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 2, interrupted.Count, "The read answers the same rows the collapse takes, so a caller can judge them before anything is written.");

        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("The engine restarted while this node was in flight.", []).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, reconciled.Count, "Only the queued and running node runs lost an executor.");
        var reconciledRunning = reconciled.Single(row => row.NodeRunId == runningId);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, reconciledRunning.Status, "The pre-collapse status is what tells the runtime what the node was doing.");
        AssertEx.Equal(DevWorkflowNodeType.Tool, reconciledRunning.NodeType);
        AssertEx.Equal(sessionId, reconciledRunning.WorkSessionId, "The session id travels with the row so the runtime needs no follow-up read per node.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Queued, reconciled.Single(row => row.NodeRunId == queuedId).Status);

        var nodeRuns = (await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).ToDictionary(nodeRun => nodeRun.Id);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRuns[queuedId].Status, "A collapsed node run goes back to Pending so the dispatcher can re-admit it.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRuns[runningId].Status);
        AssertEx.Null(nodeRuns[runningId].StartedAtUtc, "A node run about to be re-dispatched must not claim it started before the restart.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.WaitingForApproval, nodeRuns[waitingId].Status, "A gate waiting on a human survives a restart untouched.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Succeeded, nodeRuns[doneId].Status);

        var run = await store.GetRunAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowRunStatus.Running, run.Status, "Runs auto-resume, so reconciliation never moves a run's status.");

        AssertEx.Null(nodeRuns[runningId].TerminalReason, "A row sitting at Pending must not carry a terminal reason, or the UI reads the restart as this attempt's outcome.");
        AssertEx.Null(nodeRuns[runningId].FailureClass);

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, events.Count(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted), "One interrupted event per collapsed node run.");
        AssertEx.True(events.Any(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted
                                         && item.DetailJson is not null
                                         && item.DetailJson.Contains("restarted", StringComparison.Ordinal)),
            "The reason moved to the event, so it must actually be readable there.");

        var second = await store.ReconcileNonTerminalNodeRunsAsync("Second pass.", []).ConfigureAwait(false);
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
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var (seed, nodeRunId) = await SeedRunningToolAsync(store).ConfigureAwait(false);

        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                                        [
                                            new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                                nodeRunId,
                                                DevWorkflowVersions.Any,
                                                DevWorkflowNodeRunStatus.Pending,
                                                IncrementAttempt: true,
                                                Outcome: "interrupted")
                                        ])
                                    .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, reconciled.Count);
        var nodeRun = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, nodeRun.Status);
        AssertEx.Equal(expected: 2, nodeRun.Attempt, "The repair is what spends the attempt, and it committed with the collapse.");

        var events = await store.ListEventsAsync(seed.RunId).ConfigureAwait(false);
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
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var (seed, nodeRunId) = await SeedRunningToolAsync(store).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                                  [
                                      new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                          nodeRunId,
                                          long.MaxValue,
                                          DevWorkflowNodeRunStatus.Pending,
                                          IncrementAttempt: true)
                                  ]))
                          .ConfigureAwait(false);

        var stranded = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Running, stranded.Status, "Nothing committed, so the row is still what the next boot has to reconcile.");
        AssertEx.Equal(expected: 1, stranded.Attempt);
        AssertEx.Empty((await store.ListEventsAsync(seed.RunId).ConfigureAwait(false)).Where(item => item.EventType == DevWorkflowEventTypes.NodeInterrupted));

        // And the next boot finds it, repairs it, and spends exactly one attempt on the one interruption.
        AssertEx.Equal(expected: 1, (await store.ListInterruptedNodeRunsAsync().ConfigureAwait(false)).Count);
        _ = await store.ReconcileNonTerminalNodeRunsAsync("The host restarted.",
                           [
                               new TransitionDevWorkflowNodeRunCommand(seed.RunId,
                                   nodeRunId,
                                   DevWorkflowVersions.Any,
                                   DevWorkflowNodeRunStatus.Pending,
                                   IncrementAttempt: true)
                           ])
                       .ConfigureAwait(false);

        var repaired = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending, repaired.Status);
        AssertEx.Equal(expected: 2, repaired.Attempt);
    }

    /// <summary>A run with one Tool node run the host left mid-command — the row every atomicity test starts from.</summary>
    private static async Task<(DevWorkflowSeed Seed, Guid NodeRunId)> SeedRunningToolAsync(DevWorkflowStore store)
    {
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);
        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "validate", seed.RunVersion, DevWorkflowNodeType.Tool)
                                                  .ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(new TransitionDevWorkflowNodeRunCommand(seed.RunId, nodeRunId, version, DevWorkflowNodeRunStatus.Running))
                       .ConfigureAwait(false);
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
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var sessionStore = new AgentWorkSessionStore(context, TimeProvider.System);
        var seed = await DevWorkflowTestFixture.SeedRunAsync(store).ConfigureAwait(false);

        var sessionId = Guid.NewGuid();
        _ = await sessionStore.CreateAsync(new CreateWorkSessionCommand(sessionId,
                                  Guid.NewGuid(),
                                  Guid.NewGuid(),
                                  AgentWorkSessionKind.Workflow,
                                  "Research the thing",
                                  "Find out what we are building."))
                              .ConfigureAwait(false);

        var nodeRunId = Guid.NewGuid();
        var version = await DevWorkflowTestFixture.AddNodeRunAsync(store, seed.RunId, nodeRunId, "research", seed.RunVersion).ConfigureAwait(false);
        _ = await store.AttachWorkSessionAsync(new AttachDevWorkflowWorkSessionCommand(seed.RunId, nodeRunId, version, sessionId)).ConfigureAwait(false);

        AssertEx.True((await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false)).WorkSessionAvailable);

        _ = await sessionStore.DeleteAsync(sessionId).ConfigureAwait(false);

        var afterPurge = await store.GetNodeRunAsync(nodeRunId).ConfigureAwait(false);
        AssertEx.Equal(sessionId, afterPurge.WorkSessionId, "The pointer stays: it is the record that a session once existed.");
        AssertEx.False(afterPurge.WorkSessionAvailable, "A purged session must read back as recoverable state rather than fail the read.");
        AssertEx.False((await store.ListNodeRunsAsync(seed.RunId).ConfigureAwait(false)).Single().WorkSessionAvailable);
    }

    /// <summary>The Workflow session kind is admitted at both layers — the guards deny only the reserved Development kind.</summary>
    [Test]
    public async Task TheWorkSessionStore_AdmitsTheWorkflowKind()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var sessionStore = new AgentWorkSessionStore(context, TimeProvider.System);

        var created = await sessionStore.CreateAsync(new CreateWorkSessionCommand(Guid.NewGuid(),
                                            Guid.NewGuid(),
                                            Guid.NewGuid(),
                                            AgentWorkSessionKind.Workflow,
                                            "Workflow node",
                                            "Do the node's work."))
                                        .ConfigureAwait(false);
        AssertEx.Equal(AgentWorkSessionKind.Workflow, created.Kind);

        _ = await AssertEx.ThrowsAsync<ArgumentException>(() => sessionStore.CreateAsync(new CreateWorkSessionCommand(Guid.NewGuid(),
                                  Guid.NewGuid(),
                                  Guid.NewGuid(),
                                  AgentWorkSessionKind.Development,
                                  "Reserved",
                                  "Reserved.")),
                              "Development stays reserved by the series this module supersedes.")
                          .ConfigureAwait(false);
    }
}
