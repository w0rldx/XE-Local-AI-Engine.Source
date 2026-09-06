namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run command surface over the real store and a real database. Only the dispatcher signal is faked, and only
///     so a test can see it — there is no dispatcher in this slice, so a started run legitimately sits <c>Pending</c>.
/// </summary>
public sealed class GraphWorkflowRunServiceTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>
    ///     The idempotency contract, in its ordinary serial shape: the same request id answers with the same run and
    ///     writes no second one, and the replay SIGNALS — a caller that never saw the first answer is asking about a
    ///     run that may still be waiting for its first tick.
    /// </summary>
    [Test]
    public async Task StartAsync_WithTheSameRequestIdTwice_AnswersTheSameRunAndSignalsAgain()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var requestId = Guid.NewGuid();

        var first = await StartAsync(definitionId, requestId).ConfigureAwait(false);
        var second = await StartAsync(definitionId, requestId).ConfigureAwait(false);

        AssertEx.Equal(first.Run.Id, second.Run.Id, "the request id is the idempotency key, so a retry resolves to the run it already created.");
        AssertEx.Equal(expected: 3, first.NodeRuns.Count, "one Pending node run per graph node, written in the same commit as the run.");
        AssertEx.Equal(expected: 2, Signals.CountFor(first.Run.Id), "the replay signals too: the run may still be waiting for its first tick.");
    }

    /// <summary>
    ///     The race the serial test above never reaches. The service's lookup is a fast path, not a gate — both callers
    ///     can pass it — so the unique index on <c>request_id</c> is the real guarantee, and the loser has to come back
    ///     with the winner's run rather than a <c>DbUpdateException</c>.
    /// </summary>
    [Test]
    public async Task StartAsync_WithTwoConcurrentIdenticalStarts_WritesOneRunAndBothCallersGetIt()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var requestId = Guid.NewGuid();

        // Each in its own DI scope, because the store and its DbContext are scoped: one scope would serialize them
        // through a single change tracker and prove nothing about two writers.
        var both = await Task.WhenAll(StartAsync(definitionId, requestId), StartAsync(definitionId, requestId)).ConfigureAwait(false);

        AssertEx.Equal(both[0].Run.Id, both[1].Run.Id, "one run row, and both callers hold its id.");

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        AssertEx.Equal(both[0].Run.Id, AssertEx.NotNull(await store.FindRunByRequestAsync(requestId).ConfigureAwait(false)).Id);
    }

    /// <summary>
    ///     A reused request id naming a different definition is a caller bug, and answering it with the first run would
    ///     hand out a run nobody asked for.
    /// </summary>
    [Test]
    public async Task StartAsync_WithARequestIdAlreadyUsedForAnotherDefinition_Refuses()
    {
        var requestId = Guid.NewGuid();
        var first = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var second = await SeedDefinitionAsync(GraphWorkflowGraphs.BranchOnJson).ConfigureAwait(false);
        _ = await StartAsync(first, requestId).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowInvalidTransitionException>(() => StartAsync(second, requestId)).ConfigureAwait(false);
    }

    /// <summary>
    ///     The same caller bug, in the shape the fast path cannot catch. Two concurrent starts sharing a request id but
    ///     naming different definitions can BOTH miss the lookup; the loser of the unique index is then answered with
    ///     the winner's run, which is a run of a definition it never asked for. It has to be refused there too.
    /// </summary>
    [Test]
    public async Task StartAsync_WithTwoConcurrentStartsOfDifferentDefinitionsSharingARequestId_WritesOneRunAndRefusesTheOther()
    {
        var requestId = Guid.NewGuid();
        var first = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var second = await SeedDefinitionAsync(GraphWorkflowGraphs.BranchOnJson).ConfigureAwait(false);

        // Each in its own DI scope, because the store and its DbContext are scoped: one scope would serialize them
        // through a single change tracker and prove nothing about two writers.
        var outcomes = await Task.WhenAll(TryStartAsync(first, requestId), TryStartAsync(second, requestId)).ConfigureAwait(false);

        AssertEx.ContainsSingle(outcomes, outcome => outcome.Detail is not null, "one start wins the request id — either of them may.");
        AssertEx.ContainsSingle(outcomes, outcome => outcome.Refusal is not null, "and the other is refused rather than handed the winner's run.");

        var winner = AssertEx.NotNull(outcomes.Single(static outcome => outcome.Detail is not null).Detail);
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var stored = AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().FindRunByRequestAsync(requestId).ConfigureAwait(false));
        AssertEx.Equal(winner.Run.Id, stored.Id, "one run row holds the request id.");
        AssertEx.Equal(winner.Run.DefinitionId, stored.DefinitionId, "and it is a run of the definition that won, not of the one that lost.");
    }

    /// <summary>
    ///     The loser's refusal, pinned deterministically. The race above can be won SERIALLY — one start commits before
    ///     the other reads — and then the fast path refuses and the post-insert check never runs. Here the store is
    ///     substituted so only the other shape exists: nothing found by request id, and the insert answering with the
    ///     WINNER's run, which names a definition this caller never asked for. That run is refused, the dispatcher is
    ///     never signalled for it, and the refusal provably comes from AFTER the insert.
    /// </summary>
    [Test]
    public async Task StartAsync_WhenTheInsertAnswersWithAnotherDefinitionsRun_RefusesAfterTheInsertWithoutSignalling()
    {
        var definitionId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var winnerRunId = Guid.NewGuid();

        var store = Substitute.For<IGraphWorkflowStore>();
        _ = store.FindRunByRequestAsync(requestId, Arg.Any<CancellationToken>()).Returns((GraphWorkflowRunSnapshot?)null);
        _ = store.GetDefinitionAsync(definitionId, Arg.Any<CancellationToken>())
                 .Returns(new GraphWorkflowDefinitionSnapshot(definitionId,
                     "Substituted",
                     Description: null,
                     GraphWorkflowGraphs.InlineLinear,
                     "graph-hash",
                     NodeCount: 3,
                     SchemaVersion: 1,
                     Version: 1,
                     CreatedAtUtc: 0,
                     UpdatedAtUtc: 0));

        // What the store answers a LOST unique-index race with: the run that won the request id, which is a run of
        // somebody else's definition.
        _ = store.StartRunAsync(Arg.Any<StartGraphWorkflowRunCommand>(), Arg.Any<CancellationToken>())
                 .Returns(new GraphWorkflowRunSnapshot(winnerRunId,
                     requestId,
                     Guid.NewGuid(),
                     DefinitionVersion: 1,
                     "graph-hash",
                     GraphWorkflowRunStatus.Pending,
                     GraphWorkflowFailureClass.None,
                     GraphWorkflowGraphs.InlineLinear,
                     InputJson: null,
                     OutputJson: null,
                     Seq: 1,
                     Version: 1,
                     CancelRequestedAtUtc: null,
                     StartedAtUtc: null,
                     CompletedAtUtc: null,
                     CreatedAtUtc: 0));

        var signals = new RecordingGraphWorkflowDispatcherSignal();

        // The graph carries no Tool node, so the tool gate never reads the catalog: the substitute is here to satisfy
        // the constructor, and a call on it would be a bug this test would rather fail on than hide.
        var runs = new GraphWorkflowRunService(store, signals, Substitute.For<IToolInvocationService>(), Options.Create(new GraphWorkflowOptions()));

        _ = await AssertEx.ThrowsAsync<GraphWorkflowInvalidTransitionException>(() =>
                              runs.StartAsync(definitionId, requestId, inputJson: null, definitionVersion: null))
                          .ConfigureAwait(false);

        _ = store.Received(requiredNumberOfCalls: 1).StartRunAsync(Arg.Any<StartGraphWorkflowRunCommand>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 0,
            signals.CountFor(winnerRunId),
            "the refusal lands before the signal, so nothing wakes the dispatcher for a run this caller never asked for.");
    }

    /// <summary>
    ///     The decide write can lose in TWO ways and both are the same story to the caller: the compare-and-set
    ///     matching no row, and the run row's own concurrency token — which is what two operators answering at once
    ///     with different operation ids actually produce. Neither may escape as a bare run conflict: the person whose
    ///     click lost has to be told what was decided.
    ///     <para>
    ///         Everything is the REAL store behind a one-method seam that lets the other operator's answer land inside
    ///         this caller's write, so the checks above it pass exactly as they would in the race being simulated.
    ///     </para>
    /// </summary>
    [Test]
    [Arguments(GraphWorkflowRace.ConcurrencyToken)]
    [Arguments(GraphWorkflowRace.CompareAndSet)]
    public async Task DecideAsync_WhenAConcurrentAnswerLandsInsideTheWrite_RefusesWithTheStandingDecision(GraphWorkflowRace race)
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var racing = new RacingGraphWorkflowStore(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), GraphWorkflowDecisionKind.Reject, race);
        var runs = new GraphWorkflowRunService(racing,
            new RecordingGraphWorkflowDispatcherSignal(),
            Substitute.For<IToolInvocationService>(),
            Options.Create(harness.CurrentOptions()));

        var refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowGateAlreadyDecidedException>(() =>
                                runs.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, comment: null, payloadJson: null, "operator"))
                            .ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowDecisionKind.Reject, refusal.StandingDecision, "the answer that WON is what the loser is told about.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status);
        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == GraphWorkflowEventTypes.GateDecided),
            "the loser wrote nothing, so the winner's answer is the only decision on the run.");
    }

    /// <summary>
    ///     A cancel landing between the decision's checks and its write. The endpoint promised a 409, so the decision
    ///     must not commit: a decided row under a run that is draining would be overwritten to <c>Cancelled</c> while
    ///     keeping its decision columns, leaving an audit trail saying a pause was answered on a run that was cancelled.
    ///     <para>
    ///         What refuses it is the store's own re-read of the run row INSIDE the write's transaction. The service's
    ///         pre-read cannot: the cancel commits after it.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DecideAsync_WhenACancelLandsInsideTheWrite_RefusesAndCommitsNothing()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var runs = Service(scope, GraphWorkflowDecisionKind.Approve, GraphWorkflowRace.CancelledMidWrite, harness);

        var refusal = await AssertEx
                            .ThrowsAsync<GraphWorkflowRunConflictException>(() =>
                                runs.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, comment: null, payloadJson: null, "operator"))
                            .ConfigureAwait(false);

        AssertEx.Contains(refusal.Message, "Cancelling", StringComparison.Ordinal, "the cancel is the reason, so the cancel is what the refusal names.");
        AssertEx.NotEqual(GraphWorkflowNodeRunStatus.Succeeded,
            (await harness.ReadNodeRunAsync(runId, "review").ConfigureAwait(false)).Status,
            "a decision the run can no longer route must not land on the row.");
        AssertEx.Empty((await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Where(static entry => entry.EventType == GraphWorkflowEventTypes.GateDecided),
            "and nothing was audited as an answer.");
    }

    /// <summary>
    ///     Two identical requests, both past the replay lookup before either commits. The loser reads a row that is
    ///     already <c>Succeeded</c> — by its OWN operation id — so refusing on the row's status alone would 409 a
    ///     decision that did land, for a caller that did exactly the right thing.
    /// </summary>
    [Test]
    public async Task DecideAsync_WhenAnIdenticalRequestCommitsFirst_ReplaysItRatherThanRefusing()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions).ConfigureAwait(false);
        _ = await harness.AdvanceUntilQuiescentAsync(runId).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var runs = Service(scope, GraphWorkflowDecisionKind.Approve, GraphWorkflowRace.IdenticalAnswer, harness);

        var result = await runs.DecideAsync(runId, "review", Guid.NewGuid(), GraphWorkflowDecisionKind.Approve, comment: null, payloadJson: null, "operator")
                               .ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowDecisionKind.Approve, result.Decision);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, result.NodeRunStatus, "the answer the other request committed is this one's answer too.");
        AssertEx.Equal(expected: 1,
            (await harness.ReadEventsAsync(runId).ConfigureAwait(false)).Count(static entry => entry.EventType == GraphWorkflowEventTypes.GateDecided),
            "one act, one audited decision, however many times it was sent.");
    }

    /// <summary>The run service over a store whose decide loses the named race, and everything else real.</summary>
    private static GraphWorkflowRunService Service(AsyncServiceScope scope,
        GraphWorkflowDecisionKind winningDecision,
        GraphWorkflowRace race,
        GraphWorkflowHarness harness) =>
        new(new RacingGraphWorkflowStore(scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>(), winningDecision, race),
            new RecordingGraphWorkflowDispatcherSignal(),
            Substitute.For<IToolInvocationService>(),
            Options.Create(harness.CurrentOptions()));

    /// <summary>A start against a version that has since been edited answers a conflict rather than running a graph the caller never saw.</summary>
    [Test]
    public async Task StartAsync_WithAStaleDefinitionVersion_Conflicts()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => StartAsync(definitionId, Guid.NewGuid(), definitionVersion: 99))
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task StartAsync_WithAnEmptyRequestId_Refuses()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => StartAsync(definitionId, Guid.Empty)).ConfigureAwait(false);
    }

    /// <summary>
    ///     The pinned graph is what the run executes. A definition edited after a run started must not change what that
    ///     run does — which is the whole reason the run carries its own copy.
    /// </summary>
    [Test]
    public async Task StartAsync_PinsTheGraphSoALaterDefinitionEditDoesNotChangeTheRun()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        await using (var scope = Host.Factory.Services.CreateAsyncScope())
        {
            var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
            _ = await definitions.UpdateAsync(definitionId, expectedVersion: 1, name: null, description: null, GraphWorkflowGraphs.BranchOnJson)
                                 .ConfigureAwait(false);
        }

        await using var readScope = Host.Factory.Services.CreateAsyncScope();
        var run = await readScope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().GetRunAsync(started.Run.Id).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowGraphs.StartAgentEnd, run.GraphJson, "the run keeps the graph it started with, byte for byte.");
        AssertEx.Equal(expected: 1, run.DefinitionVersion, "and the version it was started against, which is what a later reader compares.");
    }

    /// <summary>
    ///     The run-start tool gate, from its passing side: both tools this graph names are inside the D6 envelope, so
    ///     the start is not the thing that refuses it. The refusals live in <c>GraphWorkflowToolValidationTests</c>.
    /// </summary>
    [Test]
    public async Task StartAsync_WithAToolNodeNamingInvocableTools_CreatesTheRun()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.ToolNode).ConfigureAwait(false);

        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Pending, started.Run.Status, "no dispatcher in this slice, so a started run sits Pending.");
        AssertEx.Contains(started.NodeRuns, nodeRun => nodeRun.Kind == GraphWorkflowNodeKind.Tool);
    }

    /// <summary>A run input over the cap is refused BEFORE the insert, so no half-started run survives the refusal.</summary>
    [Test]
    public async Task StartAsync_WithARunInputOverTheCap_RefusesAndWritesNoRun()
    {
        // A private host: the cap is host-level configuration, and a sibling starting a normal run must not see it. 1024
        // is the validator's floor, so this is the smallest cap a real node can be configured with.
        await using var factory = GraphWorkflowHostFixture.NewFactory(("GraphWorkflows:MaxRunInputBytes", "1024"));
        var definitionId = await SeedDefinitionAsync(factory, GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var requestId = Guid.NewGuid();

        await using var scope = factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        _ = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                              runs.StartAsync(definitionId, requestId, $$"""{"blob":"{{new string('x', 2048)}}"}""", definitionVersion: null))
                          .ConfigureAwait(false);

        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        AssertEx.Null(await store.FindRunByRequestAsync(requestId).ConfigureAwait(false), "a refused start leaves nothing behind for the request id to find.");
    }

    /// <summary>
    ///     The fan-out guard, refused at start rather than halfway through the graph.
    ///     <para>
    ///         The options validator refuses a node whose run cap is below its definition cap, so a definition over the
    ///         run cap can only be one that predates a tightening. It is therefore seeded through the STORE, which does
    ///         not enforce the definition cap — which is exactly the shape a graph saved under the old numbers has.
    ///     </para>
    /// </summary>
    [Test]
    public async Task StartAsync_WithMoreNodesThanARunMayInstantiate_Refuses()
    {
        await using var factory = GraphWorkflowHostFixture.NewFactory(("GraphWorkflows:MaxNodeRunsPerRun", "2"), ("GraphWorkflows:MaxNodesPerDefinition", "2"));

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        var definition = await store.CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand(Guid.NewGuid(),
                                        "Saved under a wider cap",
                                        GraphWorkflowGraphs.StartAgentEnd,
                                        NodeCount: 3))
                                    .ConfigureAwait(false);

        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        var thrown = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() =>
                                       runs.StartAsync(definition.Id, Guid.NewGuid(), inputJson: null, definitionVersion: null))
                                   .ConfigureAwait(false);
        AssertEx.Contains(thrown.Message, "3 nodes", message: "the refusal names what the graph declares against what a run may instantiate.");
    }

    /// <summary>
    ///     A cancel on a run that has not been ticked writes the intent and nothing else: the node runs are the
    ///     dispatcher's to drain, and settling them here would write a terminal status over work it cannot see.
    /// </summary>
    [Test]
    public async Task CancelAsync_OnAPendingRun_RecordsTheIntentWithoutSettlingItsNodeRuns()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var cancelled = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>().CancelAsync(started.Run.Id).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, cancelled.Run.Status, "cancel is fire-and-forget: the run drains before it is Cancelled.");
        AssertEx.True(cancelled.Run.CancelRequestedAtUtc is not null, "the intent is stamped, which is what the drain reads it off.");
        AssertEx.True(cancelled.NodeRuns.All(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Pending),
            "the node runs are left for the dispatcher's drain.");
    }

    /// <summary>
    ///     A repeat cancel is the same ask answered again, not a conflict. It mirrors the start replay: the intent is
    ///     already committed, so the second call reports where the run stands rather than refusing a caller who never
    ///     saw the first answer.
    /// </summary>
    [Test]
    public async Task CancelAsync_OnARunAlreadyCancelling_IsANoOpThatReportsTheCurrentDetail()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        var first = await runs.CancelAsync(started.Run.Id).ConfigureAwait(false);
        var repeat = await runs.CancelAsync(started.Run.Id).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, repeat.Run.Status);
        AssertEx.Equal(first.Run.Version, repeat.Run.Version, "a no-op writes nothing, so the row is the one the first cancel left.");
        AssertEx.Equal(first.Run.CancelRequestedAtUtc, repeat.Run.CancelRequestedAtUtc, "and the intent keeps the instant it was first asked at.");
    }

    [Test]
    public async Task CancelAsync_OnATerminalRun_Conflicts()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();

        // Driven terminal through the store, because there is no dispatcher in this slice to do it.
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(started.Run.Id,
                           started.Run.Version,
                           GraphWorkflowRunStatus.Failed,
                           GraphWorkflowFailureClass.NodeFailed))
                       .ConfigureAwait(false);

        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => runs.CancelAsync(started.Run.Id)).ConfigureAwait(false);
    }

    /// <summary>
    ///     The event feed is capped, and says so. A client that fell behind must be told it was cut off rather than
    ///     handed a partial log it would mistake for the whole one.
    /// </summary>
    [Test]
    public async Task ListEventsAsync_PagesFromTheWatermarkAndReportsTruncation()
    {
        await using var factory = GraphWorkflowHostFixture.NewFactory(("GraphWorkflows:EventReplayLimit", "2"));
        var definitionId = await SeedDefinitionAsync(factory, GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);

        await using var scope = factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        var started = await runs.StartAsync(definitionId, Guid.NewGuid(), inputJson: null, definitionVersion: null).ConfigureAwait(false);

        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        for (var index = 0; index < 3; index++)
        {
            _ = await store.AppendEventAsync(new AppendGraphWorkflowEventCommand(started.Run.Id,
                               GraphWorkflowVersions.Any,
                               GraphWorkflowEventTypes.RunStarted))
                           .ConfigureAwait(false);
        }

        var first = await runs.ListEventsAsync(started.Run.Id, afterSeq: 0).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, first.Events.Count);
        AssertEx.True(first.ReplayTruncated, "there are four events and the cap is two, so the page says it was cut off.");
        AssertEx.Equal(GraphWorkflowEventTypes.RunCreated, first.Events[0].EventType, "run.created is the run's first event, at the first watermark.");

        var second = await runs.ListEventsAsync(started.Run.Id, first.LastSeq).ConfigureAwait(false);
        AssertEx.False(second.ReplayTruncated, "and the next page from that watermark is the rest of them.");
        AssertEx.True(second.Events.All(entry => entry.Seq > first.LastSeq), "the watermark is exclusive, so nothing is replayed twice.");
    }

    [Test]
    public async Task ListEventsAsync_WithANegativeWatermark_Refuses()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        _ = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => runs.ListEventsAsync(started.Run.Id, afterSeq: -1)).ConfigureAwait(false);
    }

    [Test]
    public async Task GetNodeRunAsync_ReadsTheRowByItsNodeKey()
    {
        var definitionId = await SeedDefinitionAsync(GraphWorkflowGraphs.StartAgentEnd).ConfigureAwait(false);
        var started = await StartAsync(definitionId, Guid.NewGuid()).ConfigureAwait(false);

        await using var scope = Host.Factory.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();

        var nodeRun = await runs.GetNodeRunAsync(started.Run.Id, "analyze").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeKind.Agent, nodeRun.Kind);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowNotFoundException>(() => runs.GetNodeRunAsync(started.Run.Id, "nosuchnode")).ConfigureAwait(false);
    }

    private RecordingGraphWorkflowDispatcherSignal Signals => (RecordingGraphWorkflowDispatcherSignal)Host.Factory.Services.GetRequiredService<IGraphWorkflowDispatcherSignal>();

    private Task<Guid> SeedDefinitionAsync(string graphJson) =>
        SeedDefinitionAsync(Host.Factory, graphJson);

    private static async Task<Guid> SeedDefinitionAsync(TestServerWebAppFactory factory, string graphJson)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var definitions = scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>();
        var created = await definitions.CreateAsync($"Seeded {Guid.NewGuid():N}", description: null, graphJson).ConfigureAwait(false);
        return created.Id;
    }

    /// <summary>One start, with its refusal captured rather than thrown, so both racers can be inspected together.</summary>
    private async Task<StartOutcome> TryStartAsync(Guid definitionId, Guid requestId)
    {
        try
        {
            return new StartOutcome(await StartAsync(definitionId, requestId).ConfigureAwait(false), Refusal: null);
        }
        catch (GraphWorkflowInvalidTransitionException refusal)
        {
            return new StartOutcome(Detail: null, refusal);
        }
    }

    /// <summary>One start in a scope of its own, which is what makes the concurrency test two writers rather than one.</summary>
    private async Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId, Guid requestId, int? definitionVersion = null)
    {
        await using var scope = Host.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>()
                          .StartAsync(definitionId, requestId, inputJson: null, definitionVersion)
                          .ConfigureAwait(false);
    }

    /// <summary>What one racer came back with: a run, or the refusal it was given instead.</summary>
    private sealed record StartOutcome(GraphWorkflowRunDetail? Detail, GraphWorkflowInvalidTransitionException? Refusal);
}

/// <summary>
///     The real store with ONE seam: another operator's answer commits inside this caller's decide write, and the
///     write then reports the loss the way the database would — by the run row's concurrency token, or by the
///     compare-and-set matching no row. Everything else forwards, so every check the service made before the write is
///     the real one.
/// </summary>
internal sealed class RacingGraphWorkflowStore(IGraphWorkflowStore inner, GraphWorkflowDecisionKind winningDecision, GraphWorkflowRace race) : IGraphWorkflowStore
{
    private int _operationLookups;

    /// <summary>
    ///     The lookup a decide resolves its idempotency with — and, for <see cref="GraphWorkflowRace.IdenticalAnswer" />,
    ///     the moment the OTHER request commits. The first call answers as it would have a microsecond earlier, before
    ///     the winner landed, which is the only interleaving that reaches the not-waiting branch with a matching id.
    /// </summary>
    public async Task<GraphWorkflowNodeRunSnapshot?> FindNodeRunByDecisionOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default)
    {
        if (race != GraphWorkflowRace.IdenticalAnswer || Interlocked.Increment(ref _operationLookups) != 1)
        {
            return await inner.FindNodeRunByDecisionOperationAsync(runId, operationId, cancellationToken).ConfigureAwait(false);
        }

        // The same act, from a request that got there first: same id, same answer, same person.
        var waiting = await inner.GetNodeRunAsync(runId, "review", cancellationToken).ConfigureAwait(false);
        _ = await inner.DecideNodeRunAsync(new DecideGraphWorkflowNodeRunCommand(runId,
                           waiting.Id,
                           GraphWorkflowVersions.Any,
                           operationId,
                           winningDecision,
                           "operator",
                           GraphWorkflowStateMachine.PauseOutputJson(winningDecision)),
                       cancellationToken)
                       .ConfigureAwait(false);
        return null;
    }

    public async Task<GraphWorkflowMutationResult?> DecideNodeRunAsync(DecideGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default)
    {
        if (race == GraphWorkflowRace.CancelledMidWrite)
        {
            // A cancel committing between this caller's checks and its write. Delegated afterwards, so what refuses the
            // decision is the store's own in-transaction re-read rather than anything this seam decides.
            _ = await inner.TransitionRunAsync(new TransitionGraphWorkflowRunCommand(command.RunId, GraphWorkflowVersions.Any, GraphWorkflowRunStatus.Cancelling),
                               cancellationToken)
                           .ConfigureAwait(false);
            return await inner.DecideNodeRunAsync(command, cancellationToken).ConfigureAwait(false);
        }

        // The winner, committed for real between this caller's checks and its own write — with its own operation id,
        // its own answer and its own output, which is what makes it a second human act rather than a replay.
        _ = await inner.DecideNodeRunAsync(command with
                       {
                           OperationId = Guid.NewGuid(),
                           Decision = winningDecision,
                           OutputJson = GraphWorkflowStateMachine.PauseOutputJson(winningDecision)
                       },
                       cancellationToken)
                       .ConfigureAwait(false);

        return race == GraphWorkflowRace.ConcurrencyToken
            ? throw new GraphWorkflowInvalidTransitionException($"A concurrent writer moved graph workflow run '{command.RunId}' before this write could commit.")
            : null;
    }

    public Task<GraphWorkflowDefinitionSnapshot> CreateDefinitionAsync(CreateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        inner.CreateDefinitionAsync(command, cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> UpdateDefinitionAsync(UpdateGraphWorkflowDefinitionCommand command, CancellationToken cancellationToken = default) =>
        inner.UpdateDefinitionAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowDefinitionSummary>> ListDefinitionsAsync(CancellationToken cancellationToken = default) =>
        inner.ListDefinitionsAsync(cancellationToken);

    public Task<GraphWorkflowDefinitionSnapshot> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        inner.GetDefinitionAsync(definitionId, cancellationToken);

    public Task DeleteDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default) =>
        inner.DeleteDefinitionAsync(definitionId, cancellationToken);

    public Task<GraphWorkflowRunSnapshot> StartRunAsync(StartGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        inner.StartRunAsync(command, cancellationToken);

    public Task<GraphWorkflowRunSnapshot?> FindRunByRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        inner.FindRunByRequestAsync(requestId, cancellationToken);

    public Task<GraphWorkflowRunSnapshot> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        inner.GetRunAsync(runId, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status = null,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        inner.ListRunsAsync(status, limit, cancellationToken);

    public Task<int> CountActiveRunsAsync(int probeLimit, CancellationToken cancellationToken = default) =>
        inner.CountActiveRunsAsync(probeLimit, cancellationToken);

    public Task<GraphWorkflowMutationResult> TransitionRunAsync(TransitionGraphWorkflowRunCommand command, CancellationToken cancellationToken = default) =>
        inner.TransitionRunAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ListNodeRunsAsync(Guid runId, CancellationToken cancellationToken = default) =>
        inner.ListNodeRunsAsync(runId, cancellationToken);

    public Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        inner.GetNodeRunAsync(runId, nodeKey, cancellationToken);

    public Task<GraphWorkflowMutationResult> TransitionNodeRunAsync(TransitionGraphWorkflowNodeRunCommand command, CancellationToken cancellationToken = default) =>
        inner.TransitionNodeRunAsync(command, cancellationToken);

    public Task<GraphWorkflowMutationResult> AppendEventAsync(AppendGraphWorkflowEventCommand command, CancellationToken cancellationToken = default) =>
        inner.AppendEventAsync(command, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowRunEventSnapshot>> ListEventsAsync(Guid runId,
        long afterSeq = 0,
        int limit = 200,
        CancellationToken cancellationToken = default) =>
        inner.ListEventsAsync(runId, afterSeq, limit, cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ListInterruptedNodeRunsAsync(CancellationToken cancellationToken = default) =>
        inner.ListInterruptedNodeRunsAsync(cancellationToken);

    public Task<IReadOnlyList<GraphWorkflowReconciledNodeRun>> ReconcileNonTerminalNodeRunsAsync(string sanitizedReason,
        IReadOnlyList<GraphWorkflowNodeRunVerdict> verdicts,
        GraphWorkflowUnjudgedNodeRunSettlement? unjudged = null,
        CancellationToken cancellationToken = default) =>
        inner.ReconcileNonTerminalNodeRunsAsync(sanitizedReason, verdicts, unjudged, cancellationToken);
}

/// <summary>What commits inside this caller's decide, and how the store then reports the loss.</summary>
public enum GraphWorkflowRace
{
    /// <summary>Another operator's answer lands, and the run row's concurrency token refuses this write.</summary>
    ConcurrencyToken,

    /// <summary>Another operator's answer lands, and the compare-and-set matches no row.</summary>
    CompareAndSet,

    /// <summary>A cancel lands, and the store's in-transaction re-read refuses a decision the run can no longer route.</summary>
    CancelledMidWrite,

    /// <summary>This caller's OWN answer lands from a second identical request, before the replay lookup can see it.</summary>
    IdenticalAnswer
}
