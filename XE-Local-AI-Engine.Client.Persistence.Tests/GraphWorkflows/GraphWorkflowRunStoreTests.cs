namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The run half of the store: what a start commits, what the request-id index guarantees, and what restart recovery
///     does in one transaction. The definition half is <c>GraphWorkflowStoreTests</c>.
/// </summary>
public sealed class GraphWorkflowRunStoreTests
{
    /// <summary>
    ///     The insert IS the idempotency guarantee. A second start on the same caller-minted id does not throw and does
    ///     not write a second row — it comes back with the run that won, which is what the service turns into a replay.
    /// </summary>
    [Test]
    public async Task StartRun_WithARequestIdThatAlreadyWon_AnswersTheRunThatHoldsIt()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        var requestId = Guid.NewGuid();

        var first = await store.StartRunAsync(StartCommand(definition, requestId)).ConfigureAwait(false);
        var second = await store.StartRunAsync(StartCommand(definition, requestId)).ConfigureAwait(false);

        AssertEx.Equal(first.Id, second.Id, "the loser of the unique index answers with the winner rather than an error.");
        AssertEx.Equal(expected: 1, await fixture.RawTableCountAsync("graph_workflow_runs").ConfigureAwait(false));
    }

    /// <summary>
    ///     One commit: the run row, a Pending node run per seed, and the run.created event. A run that committed without
    ///     its node runs would be a durable workflow running a request nobody can reconstruct.
    /// </summary>
    [Test]
    public async Task StartRun_CommitsTheRunItsNodeRunsAndItsFirstEventTogether()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);

        var run = await store.StartRunAsync(StartCommand(definition, Guid.NewGuid())).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Pending, run.Status);
        AssertEx.Equal(GraphWorkflowTestFixture.SampleGraph, run.GraphJson, "the pinned copy comes back through the encrypt/decrypt pair intact.");
        var nodeRuns = await store.ListNodeRunsAsync(run.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, nodeRuns.Count);
        AssertEx.True(nodeRuns.All(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Pending));

        var events = await store.ListEventsAsync(run.Id).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, events.Count);
        AssertEx.Equal(GraphWorkflowEventTypes.RunCreated, events[0].EventType);
        AssertEx.Equal(expected: 1, events[0].Seq, "the run's watermark starts at one and every later write takes the next.");
    }

    /// <summary>
    ///     The delete-vs-start obligation the definition store names. The re-read runs INSIDE the insert transaction, so
    ///     a start racing a delete answers not-found instead of pinning a definition that is already gone.
    /// </summary>
    [Test]
    public async Task StartRun_AgainstADefinitionDeletedInTheMeantime_AnswersNotFoundAndWritesNoRun()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        await store.DeleteDefinitionAsync(definition.Id).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowNotFoundException>(() => store.StartRunAsync(StartCommand(definition, Guid.NewGuid()))).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await fixture.RawTableCountAsync("graph_workflow_runs").ConfigureAwait(false));
    }

    /// <summary>
    ///     A start whose definition moved on is refused for the same reason: the run's pinned copy and the version it
    ///     records have to describe the same document.
    /// </summary>
    [Test]
    public async Task StartRun_AgainstADefinitionEditedInTheMeantime_IsRefused()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        _ = await store.UpdateDefinitionAsync(new UpdateGraphWorkflowDefinitionCommand(definition.Id, ExpectedVersion: 1, "Renamed")).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowInvalidTransitionException>(() => store.StartRunAsync(StartCommand(definition, Guid.NewGuid())))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task TransitionRun_WithAStaleExpectedVersion_IsRefused()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowInvalidTransitionException>(() =>
                              store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id, ExpectedVersion: 99, GraphWorkflowRunStatus.Running)))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A node-run move writes its own event and advances the run's single watermark, which is what lets one number
    ///     answer "what changed since?" for the whole subtree.
    /// </summary>
    [Test]
    public async Task TransitionNodeRun_StampsTheRowAndTakesTheNextWatermark()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        var nodeRun = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        var invocationId = Guid.NewGuid();

        var running = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                                     nodeRun.Id,
                                     GraphWorkflowVersions.Any,
                                     GraphWorkflowNodeRunStatus.Running,
                                     InvocationId: invocationId))
                                 .ConfigureAwait(false);
        AssertEx.Equal(expected: 2, running.Sequence, "run.created took one, so this takes two.");

        var started = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.True(started.StartedAtUtc is not null, "a Running move stamps the instant the deadline is derived from.");
        AssertEx.Equal(invocationId, started.InvocationId);
        AssertEx.Null(started.CompletedAtUtc);

        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                           nodeRun.Id,
                           GraphWorkflowVersions.Any,
                           GraphWorkflowNodeRunStatus.Succeeded,
                           OutputJson: """{"status":"succeeded"}"""))
                       .ConfigureAwait(false);

        var settled = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.True(settled.CompletedAtUtc is not null, "and a terminal move stamps the one a duration is read off.");
        AssertEx.Equal("""{"status":"succeeded"}""", settled.OutputJson);

        var events = await store.ListEventsAsync(run.Id).ConfigureAwait(false);
        AssertEx.Equal("run.created, node.started, node.completed", string.Join(", ", events.Select(static entry => entry.EventType)));
        AssertEx.True(events.Select(static entry => entry.Seq).SequenceEqual(events.Select(static entry => entry.Seq).Order()),
            "the watermark is the order, which is what an exclusive replay relies on.");
    }

    /// <summary>
    ///     A move back to Pending is a clean slate: a row that carried the previous attempt's failure while it runs
    ///     again would report an outcome that has not happened yet.
    /// </summary>
    [Test]
    public async Task TransitionNodeRun_BackToPending_ClearsTheAttemptsFailureFields()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        var nodeRun = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);

        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                           nodeRun.Id,
                           GraphWorkflowVersions.Any,
                           GraphWorkflowNodeRunStatus.Running))
                       .ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                           nodeRun.Id,
                           GraphWorkflowVersions.Any,
                           GraphWorkflowNodeRunStatus.Failed,
                           FailureClass: GraphWorkflowFailureClass.NodeFailed,
                           TerminalReason: "the model said no"))
                       .ConfigureAwait(false);

        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(run.Id,
                           nodeRun.Id,
                           GraphWorkflowVersions.Any,
                           GraphWorkflowNodeRunStatus.Pending,
                           IncrementAttempt: true,
                           DetailJson: """{"failureClass":"NodeFailed"}""",
                           EventType: GraphWorkflowEventTypes.NodeRetried))
                       .ConfigureAwait(false);

        var retried = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.Equal(expected: 2, retried.Attempt, "the attempt increments in place; there is no per-attempt row.");
        AssertEx.Equal(GraphWorkflowFailureClass.None, retried.FailureClass);
        AssertEx.Null(retried.Error);
        AssertEx.Null(retried.StartedAtUtc);
        AssertEx.Null(retried.CompletedAtUtc);

        var events = await store.ListEventsAsync(run.Id).ConfigureAwait(false);
        var retry = events[^1];
        AssertEx.Equal(GraphWorkflowEventTypes.NodeRetried, retry.EventType, "the caller's event type wins over the one the status would derive.");
        AssertEx.True(AssertEx.NotNull(retry.DetailJson).Contains("NodeFailed", StringComparison.Ordinal),
            "the event is the only place the failure being re-attempted survives.");
    }

    /// <summary>
    ///     The interrupted set is exactly <c>Queued ∪ Running</c>. <c>WaitingForApproval</c> is a durable human wait, and
    ///     a reconciler that collapsed it would destroy every open pause on boot.
    /// </summary>
    [Test]
    public async Task ListInterruptedNodeRuns_TakesQueuedAndRunningOnly()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store, GraphWorkflowTestFixture.FourNodeGraph, ["queued", "running", "waiting", "pending"]).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "queued", GraphWorkflowNodeRunStatus.Queued).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "running", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "waiting", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "waiting", GraphWorkflowNodeRunStatus.WaitingForApproval).ConfigureAwait(false);

        var interrupted = await store.ListInterruptedNodeRunsAsync().ConfigureAwait(false);

        AssertEx.Equal("queued, running", string.Join(", ", interrupted.Select(static row => row.NodeKey).Order(StringComparer.Ordinal)));
    }

    /// <summary>
    ///     Recovery is ONE transaction: the collapse and every verdict commit together, so a host that dies mid-recovery
    ///     leaves the rows exactly as it found them, and the pass is idempotent — a second run finds nothing left.
    /// </summary>
    [Test]
    public async Task ReconcileNonTerminalNodeRuns_AppliesTheCollapseAndItsVerdictsInOneCommitAndIsIdempotent()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store, GraphWorkflowTestFixture.FourNodeGraph, ["queued", "running", "waiting", "pending"]).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "queued", GraphWorkflowNodeRunStatus.Queued).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "running", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "waiting", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "waiting", GraphWorkflowNodeRunStatus.WaitingForApproval).ConfigureAwait(false);

        var queued = await store.GetNodeRunAsync(run.Id, "queued").ConfigureAwait(false);
        var running = await store.GetNodeRunAsync(run.Id, "running").ConfigureAwait(false);
        var reconciled = await store.ReconcileNonTerminalNodeRunsAsync("the host restarted",
        [
            // A Queued row was never dispatched, so its verdict repairs nothing: the collapse back to Pending IS the
            // whole of it, and it costs no attempt.
            new GraphWorkflowNodeRunVerdict(queued.Id, GraphWorkflowNodeRunStatus.Queued, ObservedAttempt: 1, []),

            // The Running agent-shaped row is failed Interrupted rather than resumed: a provider turn has no durable
            // handle, so the reconciler never re-attempts it and the dispatcher's retry stage decides later.
            new GraphWorkflowNodeRunVerdict(running.Id,
                GraphWorkflowNodeRunStatus.Running,
                ObservedAttempt: 1,
                [
                    new TransitionGraphWorkflowNodeRunCommand(run.Id,
                        running.Id,
                        GraphWorkflowVersions.Any,
                        GraphWorkflowNodeRunStatus.Failed,
                        FailureClass: GraphWorkflowFailureClass.Interrupted,
                        TerminalReason: "the host restarted")
                ])
        ]).ConfigureAwait(false);

        AssertEx.Equal("queued, running", string.Join(", ", reconciled.Select(static row => row.NodeKey).Order(StringComparer.Ordinal)));
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Pending, (await store.GetNodeRunAsync(run.Id, "queued").ConfigureAwait(false)).Status);

        var failed = await store.GetNodeRunAsync(run.Id, "running").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, failed.Status, "the verdict is applied in the same commit as the collapse it repairs.");
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, failed.FailureClass);
        AssertEx.Equal(expected: 1, failed.Attempt, "an interruption costs no attempt: the retry stage decides that later.");

        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await store.GetNodeRunAsync(run.Id, "waiting").ConfigureAwait(false)).Status,
            "a pause is a durable human wait and survives a restart untouched.");

        AssertEx.Empty(await store.ReconcileNonTerminalNodeRunsAsync("the host restarted", []).ConfigureAwait(false),
            "idempotent by construction: a second pass finds no Queued or Running row left.");
    }

    /// <summary>
    ///     A row nobody could judge is settled on the LAST pass rather than left. There is no Blocked state in v1, so
    ///     "settled" means failed with the caller's class — walking away would strand a row nothing picks up again.
    /// </summary>
    [Test]
    public async Task ReconcileNonTerminalNodeRuns_WithASettlement_FailsWhateverNoVerdictMatched()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "start", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        _ = await store.ReconcileNonTerminalNodeRunsAsync("the host restarted",
                           [],
                           new GraphWorkflowUnjudgedNodeRunSettlement(GraphWorkflowFailureClass.Interrupted, "nobody could judge this row"))
                       .ConfigureAwait(false);

        var settled = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, settled.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, settled.FailureClass);
        AssertEx.Equal(expected: 1, settled.Attempt, "costing no attempt, which is the only honest price for a row nobody could judge.");
    }

    /// <summary>Without a settlement, an unjudged row is left exactly where it is for the caller's next pass.</summary>
    [Test]
    public async Task ReconcileNonTerminalNodeRuns_WithNoVerdictAndNoSettlement_LeavesTheRowAlone()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "start", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        AssertEx.Empty(await store.ReconcileNonTerminalNodeRunsAsync("the host restarted", []).ConfigureAwait(false));
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, (await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false)).Status);
    }

    /// <summary>
    ///     The three statuses that occupy a slot, counted no further than the cap asks about.
    /// </summary>
    [Test]
    public async Task CountActiveRuns_CountsTheExecutingRunsNoFurtherThanItsProbeLimit()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);

        foreach (var status in new[]
                 {
                     GraphWorkflowRunStatus.Running,
                     GraphWorkflowRunStatus.WaitingForApproval,
                     GraphWorkflowRunStatus.Cancelling
                 })
        {
            var run = await store.StartRunAsync(StartCommand(definition, Guid.NewGuid())).ConfigureAwait(false);
            _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id, run.Version, status)).ConfigureAwait(false);
        }

        AssertEx.Equal(expected: 3, await store.CountActiveRunsAsync(probeLimit: 10).ConfigureAwait(false));
        AssertEx.Equal(expected: 2, await store.CountActiveRunsAsync(probeLimit: 2).ConfigureAwait(false), "counting past the cap is work nobody reads.");
    }

    /// <summary>
    ///     <c>Pending</c> is the queue admission draws from, so counting it would count the run asking to start against
    ///     its own admission: a cap of one would admit nothing, and a Pending backlog at the cap would block every
    ///     start on the node. Terminals free their slot for the same reason they stop being ticked.
    /// </summary>
    [Test]
    public async Task CountActiveRuns_IgnoresPendingAndTerminalRuns()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        _ = await store.StartRunAsync(StartCommand(definition, Guid.NewGuid())).ConfigureAwait(false);
        var completed = await store.StartRunAsync(StartCommand(definition, Guid.NewGuid())).ConfigureAwait(false);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(completed.Id, completed.Version, GraphWorkflowRunStatus.Completed))
                       .ConfigureAwait(false);

        AssertEx.Equal(expected: 0, await store.CountActiveRunsAsync(probeLimit: 10).ConfigureAwait(false));
    }

    /// <summary>
    ///     The definition guard asks a DIFFERENT question from the concurrency cap: a <c>Pending</c> run still pins the
    ///     definition it is about to run, even though it occupies no slot yet.
    /// </summary>
    [Test]
    public async Task DeleteDefinition_WithOnlyAPendingRunOnIt_IsStillRefused()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store).ConfigureAwait(false);
        _ = await store.StartRunAsync(StartCommand(definition, Guid.NewGuid())).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowDefinitionConflictException>(() => store.DeleteDefinitionAsync(definition.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     The request id is the idempotency key and nothing else: the index does not know what definition a start
    ///     named, so the second start comes back with the FIRST run whatever it was of. Documented here because it is
    ///     what obliges the run service to re-check the definition on a lost race rather than trust the answer.
    /// </summary>
    [Test]
    public async Task StartRun_WithARequestIdAlreadyHeldByAnotherDefinitionsRun_StillAnswersTheRunThatWon()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var first = await GraphWorkflowTestFixture.SeedDefinitionAsync(store, "First").ConfigureAwait(false);
        var second = await GraphWorkflowTestFixture.SeedDefinitionAsync(store, "Second").ConfigureAwait(false);
        var requestId = Guid.NewGuid();

        var won = await store.StartRunAsync(StartCommand(first, requestId)).ConfigureAwait(false);
        var lost = await store.StartRunAsync(StartCommand(second, requestId)).ConfigureAwait(false);

        AssertEx.Equal(won.Id, lost.Id);
        AssertEx.Equal(first.Id, lost.DefinitionId, "the store answers the winner as it stands; telling the two definitions apart is the service's job.");
        AssertEx.Equal(expected: 1, await fixture.RawTableCountAsync("graph_workflow_runs").ConfigureAwait(false));
    }

    [Test]
    public async Task ListEvents_ForARunThatDoesNotExist_AnswersNotFoundRatherThanAnEmptyPage()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowNotFoundException>(() => store.ListEventsAsync(Guid.NewGuid())).ConfigureAwait(false);
    }

    /// <summary>The one column a run stores at the end, written by the transition that terminalizes it.</summary>
    [Test]
    public async Task TransitionRun_ToCompleted_WritesTheOutputAndTheCompletionInstant()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);

        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id, run.Version, GraphWorkflowRunStatus.Running)).ConfigureAwait(false);
        var running = await store.GetRunAsync(run.Id).ConfigureAwait(false);
        AssertEx.True(running.StartedAtUtc is not null);

        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id,
                           running.Version,
                           GraphWorkflowRunStatus.Completed,
                           OutputJson: """{"outcome":"completed"}"""))
                       .ConfigureAwait(false);

        var completed = await store.GetRunAsync(run.Id).ConfigureAwait(false);
        AssertEx.Equal("""{"outcome":"completed"}""", completed.OutputJson, "through the encrypt/decrypt pair, like every other document column.");
        AssertEx.True(completed.CompletedAtUtc is not null);
        AssertEx.True((await store.ListEventsAsync(run.Id).ConfigureAwait(false)).Any(static entry => entry.EventType == "run.completed"),
            "terminalizing the run writes its own event, so a reader following the log sees where it ended.");
    }

    /// <summary>
    ///     One decision, one commit: the status move, the decision columns, the composed output and the
    ///     <c>gate.decided</c> event — with the subject through the encrypt/decrypt pair like every other text column.
    /// </summary>
    [Test]
    public async Task DecideNodeRun_CommitsTheAnswerItsColumnsAndItsEventTogether()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        await ParkAsync(store, run.Id, "start").ConfigureAwait(false);
        var waiting = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        var result = await store.DecideNodeRunAsync(new DecideGraphWorkflowNodeRunCommand(run.Id,
                                    waiting.Id,
                                    GraphWorkflowVersions.Any,
                                    operationId,
                                    GraphWorkflowDecisionKind.Approve,
                                    "operator@localhost",
                                    """{"status":"succeeded","output":{"decision":"Approve"}}"""))
                                .ConfigureAwait(false);

        AssertEx.NotNull(result, "the row was waiting and undecided, so the conditional write applied.");
        var decided = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, decided.Status, "both answers succeed the pause; routing is the edges' job.");
        AssertEx.Equal<GraphWorkflowDecisionKind?>(expected: null, decided.PendingDecisionKind, "it is no longer waiting for anything.");
        AssertEx.Equal<Guid?>(operationId, decided.DecisionOperationId);
        AssertEx.Equal("operator@localhost", decided.DecidedBySubject);
        AssertEx.Equal("""{"status":"succeeded","output":{"decision":"Approve"}}""", decided.OutputJson);
        AssertEx.True(decided.CompletedAtUtc is not null);

        var events = await store.ListEventsAsync(run.Id).ConfigureAwait(false);
        AssertEx.Equal("run.created, run.started, node.started, gate.requested, gate.decided", string.Join(", ", events.Select(static entry => entry.EventType)));
        AssertEx.Equal("""{"nodeKey":"start","decision":"Approve"}""", events[^1].DetailJson, "the detail names which pause was answered and how.");
    }

    /// <summary>
    ///     The compare-and-set. A row that is no longer waiting, or already carries a decision, is simply not matched —
    ///     and losing that race is an ordinary outcome the caller re-reads its way out of, not an exception.
    /// </summary>
    [Test]
    [Arguments(GraphWorkflowNodeRunStatus.Running)]
    [Arguments(GraphWorkflowNodeRunStatus.WaitingForApproval)]
    public async Task DecideNodeRun_OnARowThatIsNotOpenToOne_WritesNothingAndAnswersNull(GraphWorkflowNodeRunStatus staged)
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        await StartRunningAsync(store, run.Id).ConfigureAwait(false);
        await MoveAsync(store, run.Id, "start", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        if (staged == GraphWorkflowNodeRunStatus.WaitingForApproval)
        {
            // Answered, so Succeeded AND carrying a decision: both halves of the predicate refuse this one.
            await MoveAsync(store, run.Id, "start", GraphWorkflowNodeRunStatus.WaitingForApproval).ConfigureAwait(false);
            _ = await DecideAsync(store, run.Id, Guid.NewGuid()).ConfigureAwait(false);
        }

        var before = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        var eventsBefore = (await store.ListEventsAsync(run.Id).ConfigureAwait(false)).Count;

        var result = await DecideAsync(store, run.Id, Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Null(result, "the conditional write matched no row, and says so rather than raising.");
        var after = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.Equal(before.Status, after.Status);
        AssertEx.Equal<Guid?>(before.DecisionOperationId, after.DecisionOperationId);
        AssertEx.Equal(eventsBefore, (await store.ListEventsAsync(run.Id).ConfigureAwait(false)).Count, "a declined mutation appends nothing.");
    }

    /// <summary>
    ///     The run row is re-read INSIDE the write's transaction, so a cancel that committed after the caller's own
    ///     check still refuses the decision. Without it the answer lands on a run that has no tick left to route it,
    ///     and the drain overwrites the decided row to Cancelled while keeping its decision columns.
    /// </summary>
    [Test]
    [Arguments(GraphWorkflowRunStatus.Cancelling)]
    [Arguments(GraphWorkflowRunStatus.Cancelled)]
    public async Task DecideNodeRun_OnceTheRunStoppedBeingLive_WritesNothingAndAnswersNull(GraphWorkflowRunStatus stopped)
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        await ParkAsync(store, run.Id, "start").ConfigureAwait(false);
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id, GraphWorkflowVersions.Any, GraphWorkflowRunStatus.Cancelling))
                       .ConfigureAwait(false);
        if (stopped == GraphWorkflowRunStatus.Cancelled)
        {
            _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(run.Id, GraphWorkflowVersions.Any, GraphWorkflowRunStatus.Cancelled))
                           .ConfigureAwait(false);
        }

        var eventsBefore = (await store.ListEventsAsync(run.Id).ConfigureAwait(false)).Count;

        AssertEx.Null(await DecideAsync(store, run.Id, Guid.NewGuid()).ConfigureAwait(false), "a run that stopped being live declines the answer.");

        var untouched = await store.GetNodeRunAsync(run.Id, "start").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval, untouched.Status);
        AssertEx.Null(untouched.DecisionOperationId, "and the decision columns stay empty, so no audit claims it was answered.");
        AssertEx.Equal(eventsBefore, (await store.ListEventsAsync(run.Id).ConfigureAwait(false)).Count);
    }

    /// <summary>
    ///     The run-wide lookup the decide surface resolves idempotency with, and the scope the filtered unique index
    ///     enforces: keyed by the run and the operation, never by the node run.
    /// </summary>
    [Test]
    public async Task FindNodeRunByDecisionOperation_AnswersTheRowThatOperationDecidedAndNothingForAnother()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        await ParkAsync(store, run.Id, "start").ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        _ = await DecideAsync(store, run.Id, operationId).ConfigureAwait(false);

        var found = AssertEx.NotNull(await store.FindNodeRunByDecisionOperationAsync(run.Id, operationId).ConfigureAwait(false));

        AssertEx.Equal("start", found.NodeKey);
        AssertEx.Null(await store.FindNodeRunByDecisionOperationAsync(run.Id, Guid.NewGuid()).ConfigureAwait(false), "an id nobody used names no row.");
        AssertEx.Null(await store.FindNodeRunByDecisionOperationAsync(Guid.NewGuid(), operationId).ConfigureAwait(false),
            "and the lookup is scoped to its run, never global.");
    }

    /// <summary>
    ///     One operation id, one decision per run — the filtered unique index. Reached only by two decides racing past
    ///     the run-wide lookup, and it reaches the caller as the same "re-read the run" story every other lost write does.
    /// </summary>
    [Test]
    public async Task DecideNodeRun_WithAnOperationIdAnotherNodeRunOfTheRunHolds_IsRefused()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var run = await StartAsync(store).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        foreach (var nodeKey in new[] { "start", "done" })
        {
            await ParkAsync(store, run.Id, nodeKey).ConfigureAwait(false);
        }

        _ = await DecideAsync(store, run.Id, operationId).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowInvalidTransitionException>(() => DecideAsync(store, run.Id, operationId, "done")).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.WaitingForApproval,
            (await store.GetNodeRunAsync(run.Id, "done").ConfigureAwait(false)).Status,
            "the refused write left the second row exactly as it found it.");
    }

    private static async Task<GraphWorkflowMutationResult?> DecideAsync(GraphWorkflowStore store, Guid runId, Guid operationId, string nodeKey = "start")
    {
        var nodeRun = await store.GetNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        return await store.DecideNodeRunAsync(new DecideGraphWorkflowNodeRunCommand(runId,
                              nodeRun.Id,
                              GraphWorkflowVersions.Any,
                              operationId,
                              GraphWorkflowDecisionKind.Approve,
                              "operator@localhost",
                              """{"status":"succeeded","output":{"decision":"Approve"}}"""))
                          .ConfigureAwait(false);
    }

    /// <summary>
    ///     A node run parked on a person, under a run that has actually STARTED. Both halves matter: the decide write
    ///     re-reads the run row and refuses one that is not live, and a Pending run never carries a waiting node run in
    ///     the first place — only a dispatcher tick produces one, and that tick moves the run to Running.
    /// </summary>
    private static async Task ParkAsync(GraphWorkflowStore store, Guid runId, string nodeKey)
    {
        await StartRunningAsync(store, runId).ConfigureAwait(false);
        await MoveAsync(store, runId, nodeKey, GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);
        await MoveAsync(store, runId, nodeKey, GraphWorkflowNodeRunStatus.WaitingForApproval).ConfigureAwait(false);
    }

    /// <summary>Idempotent: a run already Running has nothing to move, and the state machine has no self-edge.</summary>
    private static async Task StartRunningAsync(GraphWorkflowStore store, Guid runId)
    {
        if ((await store.GetRunAsync(runId).ConfigureAwait(false)).Status == GraphWorkflowRunStatus.Pending)
        {
            _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(runId, GraphWorkflowVersions.Any, GraphWorkflowRunStatus.Running))
                           .ConfigureAwait(false);
        }
    }

    private static async Task MoveAsync(GraphWorkflowStore store, Guid runId, string nodeKey, GraphWorkflowNodeRunStatus target)
    {
        var nodeRun = await store.GetNodeRunAsync(runId, nodeKey).ConfigureAwait(false);
        _ = await store.TransitionNodeRunAsync(new TransitionGraphWorkflowNodeRunCommand(runId, nodeRun.Id, GraphWorkflowVersions.Any, target))
                       .ConfigureAwait(false);
    }

    private static async Task<GraphWorkflowRunSnapshot> StartAsync(GraphWorkflowStore store,
        string graphJson = GraphWorkflowTestFixture.SampleGraph,
        IReadOnlyList<string>? nodeKeys = null)
    {
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store, graphJson: graphJson).ConfigureAwait(false);
        return await store.StartRunAsync(StartCommand(definition, Guid.NewGuid(), nodeKeys)).ConfigureAwait(false);
    }

    private static StartGraphWorkflowRunCommand StartCommand(GraphWorkflowDefinitionSnapshot definition, Guid requestId, IReadOnlyList<string>? nodeKeys = null) =>
        new(Guid.NewGuid(),
            requestId,
            definition.Id,
            definition.Version,
            definition.GraphHash,
            definition.GraphJson,
            InputJson: null,
            [.. (nodeKeys ?? ["start", "done"]).Select(static key => new GraphWorkflowNodeRunSeed(Guid.NewGuid(), key, GraphWorkflowNodeKind.Agent))]);
}
