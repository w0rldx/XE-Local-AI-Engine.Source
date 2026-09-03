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
