namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The fix loop's write against a store that is losing races, driven directly: the harness commits, and a lost
///     race is the one thing it cannot stage.
/// </summary>
public sealed class DevWorkflowRetryPolicyTests
{
    private const string FixLoop = """
                                   {
                                     "schemaVersion": 1,
                                     "nodes": [
                                       { "nodeKey": "implement", "nodeType": "DevTask", "maxAttempts": 3 },
                                       { "nodeKey": "validate", "nodeType": "Tool", "maxAttempts": 3, "retryTarget": "implement" }
                                     ],
                                     "edges": [{ "from": "implement", "to": "validate" }]
                                   }
                                   """;

    private static readonly Guid RunId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ImplementId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ValidateId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    /// <summary>
    ///     The route's lanes are stopped BEFORE its transaction opens, and a clash rolls that transaction back whole.
    ///     Leaving it there would strand cancelled attempts with nothing reset — a cancelled DevTask attempt reads to
    ///     that lane as a cancellation rather than as a round to redo — so the route is asked once more in the same
    ///     tick, with the command unchanged.
    /// </summary>
    [Test]
    public async Task ARouteThatLosesOneRace_IsAskedOnceMoreWithTheSameCommand()
    {
        var attempts = 0;
        var store = Store();
        _ = store.RouteRetryAsync(Arg.Any<RouteDevWorkflowRetryCommand>(), Arg.Any<CancellationToken>())
                 .Returns(_ => ++attempts == 1
                     ? throw new DevWorkflowConcurrencyException("A concurrent writer won the race before the route committed.")
                     : new DevWorkflowMutationResult(RunId, Sequence: 7, Version: 3, DevWorkflowRunStatus.Running, GraphRevision: 0));

        var written = await RouteAsync(store).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, attempts, "one lost race is asked again rather than left to the next sweep.");
        AssertEx.Equal(expected: 3, written, "the routing event and both resets, counted once — the re-ask is the same write, not a second one.");

        var routed = store.ReceivedCalls()
                          .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(IDevWorkflowStore.RouteRetryAsync), StringComparison.Ordinal))
                          .Select(static call => (RouteDevWorkflowRetryCommand)call.GetArguments()[0]!)
                          .ToList();
        AssertEx.True(ReferenceEquals(routed[0], routed[1]),
            "the SAME command goes back: every part of it carries version Any, so re-deriving one could only spend a second attempt composing it.");
        AssertEx.Equal(expected: 2, routed[0].Resets.Count, "the target and the node that failed under it.");
        AssertEx.True(routed[0].Resets.All(static reset => reset.IncrementAttempt),
            "and each spends exactly the one attempt the first ask had already composed.");
    }

    /// <summary>
    ///     Twice and no further. A writer that is still there on the second ask is not going away inside this tick, and
    ///     the answer that was there before is the honest one: the failure is still recorded, so the next sweep
    ///     re-derives it and routes it again.
    /// </summary>
    [Test]
    public async Task ARouteThatLosesTheRaceTwice_IsLeftToTheNextSweep()
    {
        var store = Store();
        _ = store.RouteRetryAsync(Arg.Any<RouteDevWorkflowRetryCommand>(), Arg.Any<CancellationToken>())
                 .Returns<DevWorkflowMutationResult>(_ => throw new DevWorkflowConcurrencyException("A concurrent writer won the race before the route committed."));

        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => RouteAsync(store)).ConfigureAwait(false);

        await store.Received(2).RouteRetryAsync(Arg.Any<RouteDevWorkflowRetryCommand>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await store.DidNotReceive().TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    /// <summary>
    ///     FU2-3: the state right after an operator retried a node AT the cap its definition declared — the graph
    ///     allows <c>implement</c> three attempts, they retried it on attempt 2, and the widening left the row at
    ///     attempt 3 of 4 with their reason on the inputs. A retryable failure on the attempt they bought re-attempts
    ///     rather than blocking, and ONLY because the check reads the widened cap: at the declared 3 the same row
    ///     reads "3 of 3" and <c>ReAttemptSameNodeAsync</c> blocks it. The try after theirs is an ORDINARY automatic
    ///     one, so the members are stripped exactly as a stale <c>priorFailure</c> is — leaving them would have the
    ///     next objective quote a person who said nothing about the try that just failed.
    /// </summary>
    [Test]
    public async Task AnAutomaticReAttemptAfterAnOperatorRetry_RunsToTheWidenedCapWithoutTheStaleOperatorRetryMembers()
    {
        var store = Store();
        _ = store.TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>())
                 .Returns(new DevWorkflowMutationResult(RunId, Sequence: 7, Version: 3, DevWorkflowRunStatus.Running, GraphRevision: 0));

        var implement = NodeRun(ImplementId, "implement", DevWorkflowNodeType.DevTask, DevWorkflowNodeRunStatus.Running) with
        {
            Attempt = 3,
            MaxAttempts = 4,
            InputJson = """{"requirements":"Add the negate tests.","operatorRetryReason":"Start from src/inference.","operatorRetryAttempt":3}"""
        };

        var written = await SettleSameNodeAsync(store, implement).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, written);
        var command = Transitioned(store).Single();
        AssertEx.Equal(DevWorkflowNodeRunStatus.Pending,
            command.TargetStatus,
            "the attempt the operator bought is admitted rather than refused by the cap their retry already moved: "
            + "at the definition's own 3 this row is at its cap and blocks instead.");
        AssertEx.False(command.WidenMaxAttempts, "and an automatic re-attempt buys nothing of its own.");
        var input = AssertEx.NotNull(command.InputJson);
        AssertEx.Contains(input, "requirements", message: "everything the node was admitted with still travels.");
        AssertEx.Contains(input, "priorFailure");
        AssertEx.False(input.Contains("operatorRetryReason", StringComparison.Ordinal),
            "the reason belonged to the attempt the operator retried, and nobody said anything about this one.");
        AssertEx.False(input.Contains("operatorRetryAttempt", StringComparison.Ordinal));
    }

    /// <summary>
    ///     FU3-4 race A. The automatic path checks the budget on the tick's own snapshot and then commits on a separate
    ///     call, so a human <c>Retry</c> that commits in between spends the slot it was counting on. The budget now
    ///     travels ON the command and the store re-admits it under its writer lock, which turns that lost race into a
    ///     <c>DevWorkflowInvalidTransitionException</c> — and the policy has to answer it with the same block the
    ///     pre-check would have written, not by throwing at the dispatcher.
    /// </summary>
    [Test]
    public async Task ASameNodeReAttemptRefusedByTheTransactionalBudget_BlocksWithTheBudgetReason()
    {
        var store = Store();
        _ = store.TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(static command => command.IncrementAttempt), Arg.Any<CancellationToken>())
                 .Returns<DevWorkflowMutationResult>(_ =>
                     throw new DevWorkflowRetryBudgetExceededException("This run has already spent or promised 50 re-attempts, which is as many "
                                                                       + "re-attempts as this run allows, so it cannot be retried again."));
        _ = store.TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(static command => !command.IncrementAttempt), Arg.Any<CancellationToken>())
                 .Returns(new DevWorkflowMutationResult(RunId, Sequence: 7, Version: 3, DevWorkflowRunStatus.Running, GraphRevision: 0));

        var written = await SettleSameNodeAsync(store, NodeRun(ImplementId, "implement", DevWorkflowNodeType.DevTask, DevWorkflowNodeRunStatus.Running))
            .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, written, "the block is the one write this settle makes; the refused re-attempt wrote nothing.");
        var commands = Transitioned(store);
        AssertEx.Equal(expected: 2, commands.Count);
        AssertEx.True(commands[0].MaxTotalAttempts == 50, "the run-wide budget rides on the re-attempt, or the store has nothing to admit it against.");
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, commands[1].TargetStatus, "a refused re-attempt stands the node run down rather than throwing.");
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, commands[1].FailureClass);
        AssertEx.Null(commands[1].MaxTotalAttempts, "and the block itself spends nothing, so it carries no budget of its own.");
    }

    /// <summary>
    ///     The same race on the routed path, whose cost is the whole cascade rather than one attempt. A budget refusal
    ///     is NOT a lost race: asking again would only be refused again, so the route is not re-sent — unlike the
    ///     concurrency clash <c>RouteOnceMoreOnAClashAsync</c> exists for.
    /// </summary>
    [Test]
    public async Task ARouteRefusedByTheTransactionalBudget_BlocksWithoutAskingAgain()
    {
        var store = Store();
        _ = store.RouteRetryAsync(Arg.Any<RouteDevWorkflowRetryCommand>(), Arg.Any<CancellationToken>())
                 .Returns<DevWorkflowMutationResult>(_ =>
                     throw new DevWorkflowRetryBudgetExceededException("This run has already spent or promised 50 re-attempts, which is as many "
                                                                       + "re-attempts as this run allows, so it cannot be retried again."));
        _ = store.TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>())
                 .Returns(new DevWorkflowMutationResult(RunId, Sequence: 7, Version: 3, DevWorkflowRunStatus.Running, GraphRevision: 0));

        var written = await RouteAsync(store).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, written);
        await store.Received(1).RouteRetryAsync(Arg.Any<RouteDevWorkflowRetryCommand>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);

        var routed = (RouteDevWorkflowRetryCommand)store.ReceivedCalls()
                                                        .First(call => string.Equals(call.GetMethodInfo().Name,
                                                            nameof(IDevWorkflowStore.RouteRetryAsync),
                                                            StringComparison.Ordinal))
                                                        .GetArguments()[0]!;
        AssertEx.True(routed.MaxTotalAttempts == 50, "the cascade is admitted as one act, against the run-wide budget it costs Resets.Count of.");
        AssertEx.Equal(expected: 2, routed.Resets.Count, "which is the cost the store charges: the target and the node that failed under it.");

        var blocked = Transitioned(store).Single();
        AssertEx.Equal(DevWorkflowNodeRunStatus.Blocked, blocked.TargetStatus);
        AssertEx.Equal(DevWorkflowFailureClasses.BudgetExhausted, blocked.FailureClass);
    }

    /// <summary>
    ///     Two settles against a store with one slot left, sequentially — the policy's half of the race, not the race
    ///     itself (the real interleaving is pinned in <c>DevWorkflowStoreTests</c>, against a real transaction). Both
    ///     ASK, because each pre-check reads a snapshot taken before the other's write; the store admits the first and
    ///     refuses the second, and the loser has to block rather than spend the slot twice.
    /// </summary>
    [Test]
    public async Task ASecondReAttemptAfterTheLastSlotIsSpent_BlocksInsteadOfSpendingItTwice()
    {
        // The store's own admission, standing in for the count it takes under its writer lock: the first re-attempt to
        // reach it spends the run's last slot, and every later one is refused however clean the caller's pre-check was.
        var store = Store();
        var slots = 1;
        var admitted = new List<TransitionDevWorkflowNodeRunCommand>();
        _ = store.TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(static command => command.IncrementAttempt), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     if (slots == 0)
                     {
                         throw new DevWorkflowRetryBudgetExceededException("This run has already spent or promised 1 re-attempts, which is as many "
                                                                           + "re-attempts as this run allows, so it cannot be retried again.");
                     }

                     slots--;
                     admitted.Add((TransitionDevWorkflowNodeRunCommand)call[0]!);
                     return new DevWorkflowMutationResult(RunId, Sequence: 7, Version: 3, DevWorkflowRunStatus.Running, GraphRevision: 0);
                 });
        _ = store.TransitionNodeRunAsync(Arg.Is<TransitionDevWorkflowNodeRunCommand>(static command => !command.IncrementAttempt), Arg.Any<CancellationToken>())
                 .Returns(new DevWorkflowMutationResult(RunId, Sequence: 8, Version: 4, DevWorkflowRunStatus.Running, GraphRevision: 0));

        _ = await SettleSameNodeAsync(store, NodeRun(ImplementId, "implement", DevWorkflowNodeType.DevTask, DevWorkflowNodeRunStatus.Running)).ConfigureAwait(false);
        _ = await SettleSameNodeAsync(store, NodeRun(ImplementId, "implement", DevWorkflowNodeType.DevTask, DevWorkflowNodeRunStatus.Running)).ConfigureAwait(false);

        var commands = Transitioned(store);
        AssertEx.Equal(expected: 2,
            commands.Count(static command => command is { IncrementAttempt: true, TargetStatus: DevWorkflowNodeRunStatus.Pending }),
            "both settles ASK for a re-attempt, because the pre-check reads a snapshot taken before either write.");
        AssertEx.Equal(expected: 1, admitted.Count, "but only one is admitted, because one slot is all the run had.");
        AssertEx.Equal(expected: 1,
            commands.Count(static command => command is { TargetStatus: DevWorkflowNodeRunStatus.Blocked, FailureClass: DevWorkflowFailureClasses.BudgetExhausted }),
            "and the loser stands its node run down with the budget reason rather than spending the same slot twice.");
    }

    /// <summary>
    ///     The budget catch is narrow on purpose. <c>DevWorkflowInvalidTransitionException</c> is also what the state
    ///     machine throws for an illegal move — a row settled terminal under the tick, say — and catching the base
    ///     type would report that as budget exhaustion on a run with its whole budget intact, with nothing above it
    ///     any the wiser. Only the accounting refusal is converted; everything else still reaches the dispatcher.
    /// </summary>
    [Test]
    public async Task AReAttemptRefusedForAnIllegalMove_IsNotRelabelledAsBudgetExhaustion()
    {
        var store = Store();
        _ = store.TransitionNodeRunAsync(Arg.Any<TransitionDevWorkflowNodeRunCommand>(), Arg.Any<CancellationToken>())
                 .Returns<DevWorkflowMutationResult>(_ =>
                     throw new DevWorkflowInvalidTransitionException("Node run 'implement' is Succeeded and cannot move to Pending."));

        var thrown = await AssertEx
                           .ThrowsAsync<DevWorkflowInvalidTransitionException>(() =>
                               SettleSameNodeAsync(store, NodeRun(ImplementId, "implement", DevWorkflowNodeType.DevTask, DevWorkflowNodeRunStatus.Running)))
                           .ConfigureAwait(false);

        AssertEx.False(thrown is DevWorkflowRetryBudgetExceededException, "An illegal move is not an accounting refusal.");
        AssertEx.Empty(Transitioned(store).Where(static command => command.TargetStatus == DevWorkflowNodeRunStatus.Blocked),
            "and nothing is stood down for a budget this run never spent.");
    }

    /// <summary>A retryable failure on <c>implement</c>, which declares no retry target and so re-attempts itself.</summary>
    private static Task<int> SettleSameNodeAsync(IDevWorkflowStore store, DevWorkflowNodeRunSnapshot implement)
    {
        var policy = new DevWorkflowRetryPolicy(Substitute.For<IServiceScopeFactory>(),
            Options.Create(new DevWorkflowOptions()),
            TimeProvider.System,
            NullLogger<DevWorkflowRetryPolicy>.Instance);
        return policy.SettleFailureAsync(store,
            DevWorkflowGraph.Parse(FixLoop),
            Run(),
            implement,
            [implement],
            new DevWorkflowFailure(DevWorkflowFailureClasses.ProviderError, "The coder attempt could not reach its model.", """{"failureClass":"ProviderError"}"""),
            CancellationToken.None);
    }

    private static IReadOnlyList<TransitionDevWorkflowNodeRunCommand> Transitioned(IDevWorkflowStore store) =>
    [
        .. store.ReceivedCalls()
                .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(IDevWorkflowStore.TransitionNodeRunAsync), StringComparison.Ordinal))
                .Select(static call => (TransitionDevWorkflowNodeRunCommand)call.GetArguments()[0]!)
    ];

    /// <summary>A failed <c>validate</c> routed back at the <c>implement</c> that produced what it was judging.</summary>
    private static Task<int> RouteAsync(IDevWorkflowStore store)
    {
        var policy = new DevWorkflowRetryPolicy(Substitute.For<IServiceScopeFactory>(),
            Options.Create(new DevWorkflowOptions()),
            TimeProvider.System,
            NullLogger<DevWorkflowRetryPolicy>.Instance);

        // Succeeded, which is where the node the loop routes back to almost always stands, and Running for the check
        // that just failed — so neither row asks the quiesce for a lane, and the write is all this exercises.
        var implement = NodeRun(ImplementId, "implement", DevWorkflowNodeType.DevTask, DevWorkflowNodeRunStatus.Succeeded);
        var validate = NodeRun(ValidateId, "validate", DevWorkflowNodeType.Tool, DevWorkflowNodeRunStatus.Running);
        return policy.SettleFailureAsync(store,
            DevWorkflowGraph.Parse(FixLoop),
            Run(),
            validate,
            [implement, validate],
            new DevWorkflowFailure(DevWorkflowFailureClasses.ToolCommandFailed, "The release test command reported failing tests.", "{}"),
            CancellationToken.None);
    }

    private static IDevWorkflowStore Store()
    {
        var store = Substitute.For<IDevWorkflowStore>();
        _ = store.ListDecisionsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        return store;
    }

    private static DevWorkflowRunSnapshot Run() =>
        new(RunId,
            WorkItemId: Guid.NewGuid(),
            DefinitionId: Guid.NewGuid(),
            DefinitionVersion: 1,
            DefinitionGraphHash: "hash",
            FixLoop,
            GraphRevision: 0,
            DevWorkflowRunStatus.Running,
            LastSequence: 9,
            FailureClass: null,
            TerminalReason: null,
            StartedAtUtc: 1,
            EndedAtUtc: null,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Version: 4);

    private static DevWorkflowNodeRunSnapshot NodeRun(Guid id, string nodeKey, DevWorkflowNodeType nodeType, DevWorkflowNodeRunStatus status) =>
        new(id,
            RunId,
            nodeKey,
            nodeType,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            status,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 1,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            OutputJson: null,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: 1,
            EndedAtUtc: null,
            CreatedAtUtc: 1);
}
