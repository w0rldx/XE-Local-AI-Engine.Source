namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The sweep pump is a slow safety sweep behind a wake: every path the sweep alone used to notice raises
///     <see cref="GraphWorkflowSweepWake" />, so none of them waits for the interval.
/// </summary>
/// <remarks>
///     Each run-driving test starts a replacement dispatcher's pumps on a clock it never advances, so a pass can only
///     come from a wake. The test host starts no hosted service, so the container's own pumps never compete for it.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowSweepWakeTests
{
    [Test]
    public async Task TheFirstSweepIsImmediate_ThenAnIdlePumpReadsNothingUntilItsInterval()
    {
        var clock = new ManualTimeProvider();
        var store = Substitute.For<IGraphWorkflowStore>();
        store.ListRunsAsync(Arg.Any<GraphWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<GraphWorkflowRunSnapshot>());
        await using var provider = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        await using var dispatcher = UnitDispatcher(provider, new GraphWorkflowSweepWake(), clock);

        await dispatcher.StartAsync(CancellationToken.None);
        await ParkedAsync(clock);
        AssertEx.Equal(expected: 4, ListCalls(store), "the first sweep reads every live status at once, without the clock moving.");

        // Parked on its interval timer, with no wake: nothing else can move the pump, so short of the interval it reads nothing.
        clock.Advance(TimeSpan.FromMilliseconds(4999));
        AssertEx.Equal(expected: 4, ListCalls(store), "an idle pump must not read the store before its interval.");

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await AssertEx.EventuallyAsync(() => ListCalls(store) == 8, TestBudgets.Contended, "the interval still sweeps.");
    }

    [Test]
    public async Task ARaisedWake_SweepsWithoutTheClockMoving()
    {
        var clock = new ManualTimeProvider();
        var store = Substitute.For<IGraphWorkflowStore>();
        store.ListRunsAsync(Arg.Any<GraphWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(Array.Empty<GraphWorkflowRunSnapshot>());
        await using var provider = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var wake = new GraphWorkflowSweepWake();
        await using var dispatcher = UnitDispatcher(provider, wake, clock);
        await dispatcher.StartAsync(CancellationToken.None);
        await ParkedAsync(clock);

        wake.Raise();

        await AssertEx.EventuallyAsync(() => ListCalls(store) == 8, TestBudgets.Contended, "a wake sweeps at once.");
    }

    /// <summary>
    ///     A lane reports a landing only once the work has COMPLETED and released its slot: a sweep woken any earlier would
    ///     poll a row whose work does not read as landed yet, or find the slot a Queued row is waiting on still held.
    /// </summary>
    [Test]
    public async Task ALane_ReportsALandingOnlyOnceTheWorkHasCompletedAndFreedItsSlot()
    {
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var landed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        GraphWorkflowInFlight<int>? flight = null;
        await using var lane = new GraphWorkflowInFlightLane<int>(slots: 1, onLanded: () => landed.TrySetResult(flight!.Work.IsCompleted));
        flight = await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), (_, _) => release.Task, CancellationToken.None);
        AssertEx.NotNull(flight);
        AssertEx.False(landed.Task.IsCompleted, "work still in flight has not landed.");

        release.SetResult(42);

        AssertEx.True(await landed.Task.WaitAsync(TestBudgets.Contended), "the landing is reported after the work completed.");
        AssertEx.NotNull(await lane.TryStartAsync(Guid.NewGuid(), attempt: 1, Guid.NewGuid(), (_, _) => Task.FromResult(1), CancellationToken.None),
            "and after its slot was released.");
    }

    /// <summary>
    ///     The container's tool lane raises the container's wake when its call lands. Read off the wake itself, on a clock
    ///     that never moves past the drain, so only a raise can complete the wait.
    /// </summary>
    [Test]
    public async Task ALandedToolCall_RaisesTheSweepWake()
    {
        const string tool = "probe_landing_wake";
        var clock = new ManualTimeProvider();

        // A private host: a parked call holds a node-wide lane slot.
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool
        {
            Parks = true
        });
        var runId = await harness.StartRunAsync(ToolGraph(tool));
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "call")).Status == GraphWorkflowNodeRunStatus.Running,
            "the tool node was never dispatched.");
        await harness.Tools.WhenRunningAsync(tool).WaitAsync(TestBudgets.Contended);

        // Drain the raise the call's lease flip already made, then wait on a clock nothing advances.
        var wake = harness.Services.GetRequiredService<GraphWorkflowSweepWake>();
        var drain = wake.WaitAsync(TimeSpan.FromSeconds(1), clock, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1));
        await drain.WaitAsync(TestBudgets.Contended);
        var landing = wake.WaitAsync(TimeSpan.FromHours(1), clock, CancellationToken.None);
        AssertEx.False(landing.IsCompleted, "nothing has landed yet, so nothing has raised the wake.");

        harness.Tools.ReleaseAll();

        await landing.WaitAsync(TestBudgets.Contended);
    }

    /// <summary>A run that ends frees its concurrent-run place, and the Pending run behind it starts on the wake that frees.</summary>
    [Test]
    public async Task ARunThatEnds_StartsThePendingRunBehindItWithoutTheClockMoving()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxConcurrentRuns", "1"));
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.InlineLinear);
        var first = await harness.StartRunOfAsync(definitionId);
        var second = await harness.StartRunOfAsync(definitionId);

        await StartReplacementPumpAsync(harness, clock);

        await AssertEx.EventuallyAsync(async () => await BothAsync(harness, first, second, GraphWorkflowRunStatus.Completed),
            TestBudgets.Contended,
            "the run held behind the cap never started once the first one ended.");
    }

    /// <summary>A run that parks on a person gives up its place too, and the Pending run behind it starts on that wake.</summary>
    [Test]
    public async Task ARunThatParks_StartsThePendingRunBehindItWithoutTheClockMoving()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxConcurrentRuns", "1"));
        var definitionId = await harness.SeedDefinitionAsync(GraphWorkflowGraphs.PauseTwoDecisions);
        var first = await harness.StartRunOfAsync(definitionId);
        var second = await harness.StartRunOfAsync(definitionId);

        await StartReplacementPumpAsync(harness, clock);

        await AssertEx.EventuallyAsync(async () => await BothAsync(harness, first, second, GraphWorkflowRunStatus.WaitingForApproval),
            TestBudgets.Contended,
            "the run held behind the cap never started once the first one parked.");
    }

    /// <summary>A run nothing signalled — its command's signal went to the recorder — is still found by the interval sweep.</summary>
    [Test]
    public async Task AnUnsignalledRun_IsStillRecoveredByTheIntervalSweep()
    {
        var clock = new ManualTimeProvider();
        await using var harness = new GraphWorkflowHarness();
        var dispatcher = harness.CreateReplacementDispatcher(StubNodeRuntimeSettings.Create().WithGraphWorkflowsEnabled(true).Build(),
            clock,
            new GraphWorkflowSweepWake());
        await dispatcher.StartAsync(CancellationToken.None);
        await ParkedAsync(clock);

        // Started after the empty first sweep parked the pump: no run has ended and no lane exists, so no wake can be raised.
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.InlineLinear);
        AssertEx.Equal(GraphWorkflowRunStatus.Pending, (await harness.ReadRunAsync(runId)).Status);

        clock.Advance(TimeSpan.FromMilliseconds(harness.Services.GetRequiredService<IOptions<GraphWorkflowOptions>>().Value.DispatchIntervalMilliseconds));

        await AssertEx.EventuallyAsync(async () => (await harness.ReadRunAsync(runId)).Status == GraphWorkflowRunStatus.Completed,
            TestBudgets.Contended,
            "the interval sweep never found the run.");
    }

    /// <summary>A replacement dispatcher whose clock never moves, on a wake of its own, with its pumps running.</summary>
    private static async Task StartReplacementPumpAsync(GraphWorkflowHarness harness, TimeProvider clock)
    {
        var dispatcher = harness.CreateReplacementDispatcher(StubNodeRuntimeSettings.Create().WithGraphWorkflowsEnabled(true).Build(),
            clock,
            new GraphWorkflowSweepWake());
        await dispatcher.StartAsync(CancellationToken.None);
    }

    /// <summary>Completes once the pump has armed its interval timer, so the clock is never moved past a wait nobody holds.</summary>
    private static Task ParkedAsync(ManualTimeProvider clock) =>
        AssertEx.EventuallyAsync(() => clock.ArmedTimerCount >= 1, TestBudgets.Contended, "the pump never parked on its interval timer.");

    private static async Task<bool> BothAsync(GraphWorkflowHarness harness, Guid first, Guid second, GraphWorkflowRunStatus status) =>
        (await harness.ReadRunAsync(first)).Status == status && (await harness.ReadRunAsync(second)).Status == status;

    private static int ListCalls(IGraphWorkflowStore store) =>
        store.ReceivedCalls().Count(call => string.Equals(call.GetMethodInfo().Name, nameof(IGraphWorkflowStore.ListRunsAsync), StringComparison.Ordinal));

    private static GraphWorkflowDispatcher UnitDispatcher(ServiceProvider provider, GraphWorkflowSweepWake wake, TimeProvider clock)
    {
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetGraphWorkflowsEnabledAsync(Arg.Any<CancellationToken>()).Returns(true);
        var options = Options.Create(new GraphWorkflowOptions());
        return new GraphWorkflowDispatcher(provider.GetRequiredService<IServiceScopeFactory>(),
            new GraphWorkflowInlineExecutor(options),
            [],
            wake,
            options,
            settings,
            clock,
            NullLogger<GraphWorkflowDispatcher>.Instance);
    }

    private static string ToolGraph(string toolName) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "call", "kind": "Tool", "config": { "toolName": "{{toolName}}" } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "call" },
              { "key": "e2", "from": "call", "to": "done" }
            ]
          }
          """;
}
