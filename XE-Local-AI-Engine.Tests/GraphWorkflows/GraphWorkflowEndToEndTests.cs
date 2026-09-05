namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One whole run of the live-validation graph, through every kind this slice ships: <c>Start</c>, an
///     <c>Agent</c> turn under a response schema, a <c>Condition</c> routing on the JSON that turn parsed, the two
///     branches it chooses between, a <c>Join</c> and an <c>End</c>.
///     <para>
///         The invocation runner is the only fake. The grammar itself is NOT proven here and cannot be — llama.cpp
///         compiles a response schema to GBNF server-side and no in-process double stands in for that, which is why
///         the live round owns that evidence.
///     </para>
/// </summary>
public sealed class GraphWorkflowEndToEndTests
{
    private const string AnalyzeInstructions = "Judge whether this needs review.";

    /// <summary>
    ///     The agent's answer routes the run: <c>requiresReview</c> true takes the labelled <c>yes</c> edge, the
    ///     <c>no</c> branch is skipped with a reason naming why, the join proceeds on the branch that arrived, and the
    ///     End node's own output becomes the run's result.
    /// </summary>
    [Test]
    public async Task TheLiveValidationGraph_RunsToCompletedAndRecordsTheBranchItTook()
    {
        // A host of this test's own: both tests here drive the SAME graph, so they script the same prompt, and a
        // shared fake would hand one of them the other's answer.
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(AnalyzeInstructions, new GraphWorkflowScriptedTurn(Text: """{"requiresReview":true,"summary":"worth a look"}"""));

        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.AgentBranchJoin, """{"topic":"the overnight logs"}""").ConfigureAwait(false);
        var run = await AdvanceUntilRunTerminalAsync(harness, runId).ConfigureAwait(false);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, run.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.None, run.FailureClass);

        var nodeRuns = await harness.ReadNodeRunsAsync(runId).ConfigureAwait(false);
        AssertEx.Empty(nodeRuns.Where(static nodeRun => !GraphWorkflowStateMachine.IsTerminal(nodeRun.Status)), "every node run of a finished run is terminal.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, Node(nodeRuns, "analyze").Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, Node(nodeRuns, "review").Status);
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Skipped, Node(nodeRuns, "quick").Status, "the branch the condition did not take is skipped, not run.");
        AssertEx.Contains(Node(nodeRuns, "quick").Error, "check", message: "and the skip names the upstream that routed elsewhere.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, Node(nodeRuns, "merge").Status, "an Any join proceeds on the branch that arrived.");

        AssertEx.Equal("yes", Branch(Node(nodeRuns, "check")), "the Condition records the labelled edge its own document fired.");
        AssertEx.Null(Branch(Node(nodeRuns, "analyze")), "the Agent's own out-edge is unconditional, and an edge that accepts everything names no branch.");
        AssertEx.True(Output(Node(nodeRuns, "analyze")).GetProperty("json").GetProperty("requiresReview").GetBoolean(),
            "the routing read the JSON the turn parsed, not its text.");

        AssertEx.Contains(run.OutputJson, "worth a look", message: "the run's result is the End node's own output document.");
    }

    /// <summary>
    ///     The event log is the run's account of itself: <c>run.created</c> opens it, <c>run.completed</c> closes it,
    ///     every node's start and finish are in between, and every committed change was announced to whoever is
    ///     watching.
    /// </summary>
    [Test]
    public async Task AFinishedRun_LeavesAnOrderedEventLogAndAPingForEveryCommit()
    {
        // A host of this test's own: both tests here drive the SAME graph, so they script the same prompt, and a
        // shared fake would hand one of them the other's answer.
        await using var harness = GraphWorkflowHarness.PrivateAgentHost();
        harness.Invocations.Script(AnalyzeInstructions, new GraphWorkflowScriptedTurn(Text: """{"requiresReview":false,"summary":"nothing to see"}"""));

        var runId = await harness.StartRunAsync(GraphWorkflowGraphs.AgentBranchJoin, """{"topic":"a quiet night"}""").ConfigureAwait(false);
        _ = await AdvanceUntilRunTerminalAsync(harness, runId).ConfigureAwait(false);

        var events = await harness.ReadEventsAsync(runId).ConfigureAwait(false);
        AssertEx.Equal(GraphWorkflowEventTypes.RunCreated, events[0].EventType);
        AssertEx.Equal(GraphWorkflowEventTypes.RunStarted, events[1].EventType);
        AssertEx.Equal(GraphWorkflowEventTypes.RunCompleted, events[^1].EventType);
        AssertEx.Contains(events, entry => entry.EventType == GraphWorkflowEventTypes.NodeQueued && entry.NodeKey == "analyze");
        AssertEx.Contains(events, entry => entry.EventType == GraphWorkflowEventTypes.NodeStarted && entry.NodeKey == "analyze");
        AssertEx.Contains(events, entry => entry.EventType == GraphWorkflowEventTypes.NodeCompleted && entry.NodeKey == "analyze");
        AssertEx.Contains(events, entry => entry.EventType == GraphWorkflowEventTypes.NodeSkipped && entry.NodeKey == "review");
        AssertEx.True(events.Zip(events.Skip(count: 1)).All(static pair => pair.Second.Seq > pair.First.Seq), "sequences strictly increase in the order they committed.");

        // One ping per committed change EXCEPT the start, which announces nothing: nobody can be subscribed to a run
        // that does not exist yet.
        var pings = Publisher(harness).PingsFor(runId);
        AssertEx.Equal(events.Count - 1, pings.Count);
        AssertEx.Empty(pings.Where(static ping => ping.Kind == GraphWorkflowChangeKind.Gate), "this slice has no gates to announce.");
    }

    private static async Task<GraphWorkflowRunSnapshot> AdvanceUntilRunTerminalAsync(GraphWorkflowHarness harness, Guid runId, int maxTicks = 80)
    {
        for (var tick = 0; tick < maxTicks; tick++)
        {
            var run = await harness.ReadRunAsync(runId).ConfigureAwait(false);
            if (GraphWorkflowStateMachine.IsTerminal(run.Status))
            {
                return run;
            }

            _ = await harness.AdvanceAsync(runId).ConfigureAwait(false);
        }

        throw new AssertionException($"Run {runId} had not settled after {maxTicks} ticks.");
    }

    private static GraphWorkflowNodeRunSnapshot Node(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns, string nodeKey) =>
        nodeRuns.SingleOrDefault(nodeRun => string.Equals(nodeRun.NodeKey, nodeKey, StringComparison.Ordinal))
        ?? throw new AssertionException($"The run carries no node run for '{nodeKey}'.");

    private static JsonElement Output(GraphWorkflowNodeRunSnapshot nodeRun)
    {
        using var document = JsonDocument.Parse(AssertEx.NotNull(nodeRun.OutputJson, $"'{nodeRun.NodeKey}' settled without an output document."));
        return document.RootElement.GetProperty("output").Clone();
    }

    private static string? Branch(GraphWorkflowNodeRunSnapshot nodeRun)
    {
        using var document = JsonDocument.Parse(AssertEx.NotNull(nodeRun.OutputJson, $"'{nodeRun.NodeKey}' settled without an output document."));
        return document.RootElement.GetProperty("branch").GetString();
    }

    private static RecordingGraphWorkflowEventPublisher Publisher(GraphWorkflowHarness harness) =>
        (RecordingGraphWorkflowEventPublisher)harness.Services.GetRequiredService<IGraphWorkflowEventPublisher>();
}
