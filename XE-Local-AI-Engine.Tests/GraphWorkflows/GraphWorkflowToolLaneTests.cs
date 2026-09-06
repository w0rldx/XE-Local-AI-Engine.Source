namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The tool lane's own contract, as opposed to what a tool node computes: the slot it takes, the entry that
///     outlives the work until a poll has SEEN it land, the stop that answers no on a repeat, the entry a retry
///     supersedes, and what a restart leaves behind.
///     <para>
///         Every test here takes a host of its own. A parked call holds a lane slot and
///         <c>FakeGraphWorkflowToolInvocation.ReleaseAll</c> is node-wide, so on a shared host either would be every
///         sibling's business too — and the restart leg sweeps every in-flight node run in the DATABASE rather than
///         one run's.
///     </para>
/// </summary>
public sealed class GraphWorkflowToolLaneTests
{
    /// <summary>
    ///     One dispatch, start to finish: the row is <c>Queued</c> saying what it waits for and <c>Running</c> once the
    ///     call holds its slot, both on the log. A re-offer of a row this lane is already driving writes nothing and
    ///     starts no second call — the check that keeps a repaired row from spending a second invocation.
    /// </summary>
    [Test]
    public async Task ADispatchedCall_GoesQueuedThenRunning_AndAReOfferStartsNoSecondCall()
    {
        const string tool = "probe_dispatch";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(Parks: true));
        var runId = await DispatchedToolRunAsync(harness, tool).ConfigureAwait(false);

        var call = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, call.Status, "the call holds its slot, so the row may honestly say so.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false),
            "node.queued, node.started",
            message: "the queue is on the log even when the slot was free a line later.");

        AssertEx.Equal(expected: 0, await ReDispatchAsync(harness, runId, "call").ConfigureAwait(false), "a row already being driven has nothing to write.");
        AssertEx.Equal(expected: 1, harness.Tools.CallCountFor(tool), "and nothing to invoke a second time.");
    }

    /// <summary>
    ///     The whole reason the lane exists: a call that takes ten minutes parks on its own task, and every other run
    ///     keeps advancing through the dispatcher's tick meanwhile.
    /// </summary>
    [Test]
    public async Task ALongCall_DoesNotHoldUpASecondRunsTick()
    {
        const string slow = "probe_parked";
        const string quick = "probe_prompt";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(slow, new GraphWorkflowScriptedTool(Parks: true));

        var parked = await DispatchedToolRunAsync(harness, slow).ConfigureAwait(false);

        var other = await harness.StartRunAsync(Graph(quick)).ConfigureAwait(false);
        await harness.AdvanceUntilAsync(other,
                async () => (await harness.ReadRunAsync(other).ConfigureAwait(false)).Status == GraphWorkflowRunStatus.Completed,
                "the second run never completed while the first one's call was parked.")
            .ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running,
            (await harness.ReadNodeRunAsync(parked, "call").ConfigureAwait(false)).Status,
            "and the parked call was still parked the whole time, so the second run really did overtake it.");
    }

    /// <summary>
    ///     The entry outlives the work until a poll has seen it land, and is given up only once the settling write has
    ///     COMMITTED. Consuming it first would spend the answer on a write that may throw, and the next poll would then
    ///     find no entry and report "the host stopped" about a call that finished perfectly.
    /// </summary>
    [Test]
    public async Task ALandedCall_KeepsItsEntryUntilAPollHasSettledTheRow()
    {
        const string tool = "probe_settle";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(Parks: true));
        var runId = await DispatchedToolRunAsync(harness, tool).ConfigureAwait(false);

        var call = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        var executor = Executor(harness);
        AssertEx.True(executor.IsInFlight(call.Id));

        harness.Tools.ReleaseAll();

        // Nothing has ticked, and nothing but a poll consumes an entry — so however far the work has got, it is still
        // this lane's to settle.
        AssertEx.True(executor.IsInFlight(call.Id), "the entry is the poll's to give up, not the work's.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Running, (await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).Status);

        await harness.AdvanceUntilAsync(runId,
                async () => (await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).Status == GraphWorkflowNodeRunStatus.Succeeded,
                "the released call never settled.")
            .ConfigureAwait(false);

        AssertEx.False(executor.IsInFlight(call.Id), "and once the settle committed, the entry is gone.");
    }

    /// <summary>
    ///     A stop asks once. The repeat answers <see langword="false" />, and that is the whole point rather than
    ///     tidiness: the drain reaches this every tick until a poll sees the call land, and a lane answering yes each
    ///     time would spin the run for the whole duration of the work.
    /// </summary>
    [Test]
    public async Task StoppingACall_AsksOnceAndSettlesTheRowCancelled()
    {
        const string tool = "probe_stopped_lane";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(Parks: true));
        var runId = await DispatchedToolRunAsync(harness, tool).ConfigureAwait(false);

        var call = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        var executor = Executor(harness);
        AssertEx.True(await executor.StopAsync(call.Id).ConfigureAwait(false), "the first ask is the one that actually cancels.");
        AssertEx.False(await executor.StopAsync(call.Id).ConfigureAwait(false), "and the repeat is not work, which is what keeps a drain from spinning.");

        await harness.AdvanceUntilAsync(runId,
                async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).Status),
                "the stopped call never settled.")
            .ConfigureAwait(false);

        var settled = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Cancelled, settled.Status, "a call that was asked to stop was cancelled, not failed.");
        AssertEx.Equal(GraphWorkflowFailureClass.Cancelled, settled.FailureClass);
    }

    /// <summary>
    ///     An entry whose row has moved on is dropped before anything is polled. Without it the registry would claim to
    ///     be driving a row a retry has already re-attempted, and settle one attempt off another's answer.
    /// </summary>
    [Test]
    public async Task ForgetSuperseded_DropsAnEntryWhoseRowMovedOn()
    {
        const string tool = "probe_superseded";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        harness.Tools.Script(tool, new GraphWorkflowScriptedTool(Parks: true));
        var runId = await DispatchedToolRunAsync(harness, tool).ConfigureAwait(false);

        var call = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        var executor = Executor(harness);
        AssertEx.True(executor.IsInFlight(call.Id));

        await executor.ForgetSupersededAsync([
            call with
            {
                Attempt = call.Attempt + 1
            }
        ]).ConfigureAwait(false);

        AssertEx.False(executor.IsInFlight(call.Id), "the entry belongs to the attempt before, and is not an answer about this one.");
    }

    /// <summary>
    ///     A <c>Running</c> Tool row with no entry behind it is what a host death leaves: the call was an in-process
    ///     await with nothing durable to resume, so recovery fails it <c>Interrupted</c> rather than collapsing it — and
    ///     the dispatcher's retry stage, which knows nothing about restarts, spends the node's second attempt on it.
    /// </summary>
    [Test]
    public async Task AnInterruptedRunningToolRow_IsFailedInterruptedAndThenReAttempted()
    {
        const string tool = "probe_restart";
        await using var harness = GraphWorkflowHarness.PrivateToolHost();
        var runId = await harness.StartRunAsync(Graph(tool)).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);

        // Staged through the store rather than by dispatching: what a crash leaves is a Running row whose lane holds
        // nothing, and a row this process really is driving would be settled by the poll instead.
        await harness.TransitionNodeRunAsync(runId, "call", GraphWorkflowNodeRunStatus.Running).ConfigureAwait(false);

        await RestartAsync(harness).ConfigureAwait(false);

        var failed = await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, failed.Status, "never resume a tool call: it died with the process.");
        AssertEx.Equal(GraphWorkflowFailureClass.Interrupted, failed.FailureClass, "the plain class — recovery knows nothing about the node's attempt budget.");
        AssertEx.Equal(expected: 1, failed.Attempt, "the reconciler never re-attempts; the dispatcher's retry stage decides that.");
        AssertEx.Contains(await harness.ReadEventTrailAsync(runId).ConfigureAwait(false), "node.interrupted, node.failed");

        await harness.AdvanceUntilAsync(runId,
                async () => (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status == GraphWorkflowRunStatus.Completed,
                "the re-attempted tool node never carried the run to its end.")
            .ConfigureAwait(false);

        AssertEx.Equal(expected: 2,
            (await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).Attempt,
            "a Tool node gets three attempts by default, and the restart spent the second one.");
    }

    /// <summary>
    ///     The bound, observed. A tool call has no node-wide bottleneck of its own, so a <c>Parallel</c> node feeding
    ///     more Tool nodes than the lane has slots would otherwise fire all of them at once. At most the configured
    ///     number run; the rest say they are queued; and every one of them still finishes, because a permit freed is a
    ///     permit the next tick hands on.
    /// </summary>
    [Test]
    public async Task AFanOutWiderThanTheLane_RunsAtMostItsSlotsAndStillFinishesEveryNode()
    {
        // A private host, and the cap is the thing under test rather than incidental: two slots against three nodes.
        await using var harness = GraphWorkflowHarness.PrivateToolHost(("GraphWorkflows:MaxConcurrentRuns", "2"));
        harness.Tools.Script("probe_fanout", new GraphWorkflowScriptedTool(Parks: true));
        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.ToolFanOut).ConfigureAwait(false);

        await harness.AdvanceUntilAsync(runId,
                async () => (await ToolRunsAsync(harness, runId).ConfigureAwait(false)).All(static nodeRun => nodeRun.Status != GraphWorkflowNodeRunStatus.Pending),
                "the three tool nodes were never all admitted.")
            .ConfigureAwait(false);

        var admitted = await ToolRunsAsync(harness, runId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, admitted.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Running), "two slots, two calls.");
        AssertEx.Equal(expected: 1, admitted.Count(static nodeRun => nodeRun.Status == GraphWorkflowNodeRunStatus.Queued), "and the third says what it is waiting for.");

        harness.Tools.ReleaseAll();
        await harness.AdvanceUntilAsync(runId,
                async () => (await harness.ReadRunAsync(runId).ConfigureAwait(false)).Status == GraphWorkflowRunStatus.Completed,
                "a queued tool node never took the permit the settled ones freed.")
            .ConfigureAwait(false);

        AssertEx.Empty((await ToolRunsAsync(harness, runId).ConfigureAwait(false)).Where(static nodeRun => nodeRun.Status != GraphWorkflowNodeRunStatus.Succeeded),
            "every node ran in the end; the bound is a queue, not a refusal.");
    }

    /// <summary>A linear <c>Start → Tool → End</c> graph over one named tool.</summary>
    private static string Graph(string toolName) =>
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

    /// <summary>A run ticked up to and including the tick that dispatches its tool node, with the call in flight.</summary>
    private static async Task<Guid> DispatchedToolRunAsync(GraphWorkflowHarness harness, string toolName)
    {
        var runId = await harness.StartRunAsync(Graph(toolName)).ConfigureAwait(false);
        await harness.AdvanceUntilAsync(runId,
                async () => (await harness.ReadNodeRunAsync(runId, "call").ConfigureAwait(false)).Status == GraphWorkflowNodeRunStatus.Running,
                $"the tool node naming '{toolName}' was never dispatched.")
            .ConfigureAwait(false);
        await harness.Tools.WhenRunningAsync(toolName).WaitAsync(TestBudgets.Contended).ConfigureAwait(false);
        return runId;
    }

    /// <summary>
    ///     Re-offers a node run to its lane exactly as an admission would, which is the only way to reach the
    ///     re-entrancy check: the dispatcher itself offers a row only while it is <c>Pending</c> or <c>Queued</c>.
    /// </summary>
    private static async Task<int> ReDispatchAsync(GraphWorkflowHarness harness, Guid runId, string nodeKey)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
        var run = await store.GetRunAsync(runId).ConfigureAwait(false);
        var graph = GraphWorkflowGraph.Parse(run.GraphJson);
        var nodeRun = (await store.ListNodeRunsAsync(runId).ConfigureAwait(false))
            .Single(candidate => string.Equals(candidate.NodeKey, nodeKey, StringComparison.Ordinal));
        return await Executor(harness).DispatchAsync(store, run, graph, graph.Nodes[nodeKey], nodeRun, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    ///     A restart, in the order the composition root registers it: recovery makes the stranded node runs judgeable
    ///     again, then a fresh dispatcher takes over. The reconciler is constructed by hand because the test host
    ///     strips every hosted service.
    /// </summary>
    private static async Task RestartAsync(GraphWorkflowHarness harness)
    {
        await new GraphWorkflowStartupReconciler(harness.Services.GetRequiredService<IServiceScopeFactory>(),
                  Options.Create(harness.CurrentOptions()),
                  harness.Services.GetRequiredService<ILogger<GraphWorkflowStartupReconciler>>())
              .StartAsync(CancellationToken.None)
              .ConfigureAwait(false);

        _ = harness.CreateReplacementDispatcher();
    }

    private static async Task<IReadOnlyList<GraphWorkflowNodeRunSnapshot>> ToolRunsAsync(GraphWorkflowHarness harness, Guid runId) =>
        [.. (await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false)).Where(static nodeRun => nodeRun.Kind == GraphWorkflowNodeKind.Tool)];

    private static IGraphWorkflowNodeExecutor Executor(GraphWorkflowHarness harness) =>
        harness.Services.GetServices<IGraphWorkflowNodeExecutor>().Single(static executor => executor.Owns(GraphWorkflowNodeKind.Tool));
}
