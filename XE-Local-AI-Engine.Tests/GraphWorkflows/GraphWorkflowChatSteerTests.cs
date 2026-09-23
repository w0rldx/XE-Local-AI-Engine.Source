namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;
using XE_Local_AI_Engine.Tests.Testing;
using static GraphWorkflowChatTestSupport;

/// <summary>
///     What the dispatcher does with a steer: re-run the running Agent on the SAME attempt with the steering section,
///     cancelling the superseded turn — or, when the row settled first, record the steer as ignored and reset nothing.
/// </summary>
/// <remarks>Serial: every test parks a turn on the host's one invocation slot, and in parallel they starve each other's agents.</remarks>
[Category(TestCategories.Integration)]
[NotInParallel("ChatSteerLease")]
public sealed class GraphWorkflowChatSteerTests
{
    [ClassDataSource<GraphWorkflowAgentHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowAgentHostFixture Host { get; init; }

    [Test]
    public async Task ASteerMidAgent_ReRunsTheSameAttemptWithTheSteeringSection_AndCancelsTheSupersededTurn()
    {
        const string instructions = "steer-mid-agent";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.ScriptSequence(instructions,
            new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks },
            new GraphWorkflowScriptedTurn { Text = "the steered analysis" });
        var (runId, conversationId) = await StartRunningAsync(harness, instructions);
        var superseded = (await harness.ReadNodeRunAsync(runId, "analyze")).InvocationId
                         ?? throw new AssertionException("a running Agent row carries its invocation id.");
        var operationId = Guid.NewGuid();

        using var steered = await PostSteerAsync(Host.Factory, runId, "analyze", operationId, "focus on the failing tests");
        AssertEx.Equal(HttpStatusCode.Accepted, steered.StatusCode);
        await harness.AdvanceUntilAsync(runId, async () => (await harness.ReadRunAsync(runId)).Status == GraphWorkflowRunStatus.Completed, "the steered run never completed");

        var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze");
        AssertEx.Equal(1, nodeRun.Attempt, "a steer re-runs the attempt; it never spends one, so MaxTotalAttempts is untouched.");
        AssertEx.Equal(1, nodeRun.Steering.Count);
        AssertEx.Equal<bool?>(true, nodeRun.Steering[0].Applied);
        AssertEx.True(harness.Invocations.Cancelled.Contains(superseded), "the superseded turn's invocation was cancelled through the lane's discard.");
        var prompts = harness.Invocations.Packages.Select(static package => package.ConversationContext[0].Content)
                             .Where(static prompt => prompt.Contains(instructions, StringComparison.Ordinal))
                             .ToList();
        AssertEx.Equal(2, prompts.Count, "one turn before the steer, one after.");
        AssertEx.False(prompts[0].Contains("## Operator steering", StringComparison.Ordinal), "an unsteered row's prompt is unchanged.");
        AssertEx.True(prompts[1].Contains("## Operator steering", StringComparison.Ordinal) && prompts[1].EndsWith("1. focus on the failing tests", StringComparison.Ordinal),
            prompts[1]);
        var trail = await harness.ReadEventTrailAsync(runId);
        AssertEx.False(trail.Contains(GraphWorkflowEventTypes.NodeRetried, StringComparison.Ordinal), trail);
        var steeredEvent = (await harness.ReadEventsAsync(runId)).Single(static entry => entry.EventType == GraphWorkflowEventTypes.NodeSteered);
        AssertEx.Equal(operationId, GraphWorkflowDocuments.Resolve(steeredEvent.DetailJson, "operationId")?.GetGuid() ?? Guid.Empty);
        AssertEx.Equal("focus on the failing tests", GraphWorkflowDocuments.Resolve(steeredEvent.DetailJson, "message")?.GetString(), "the feed renders a running node's steer from the event.");
        AssertEx.Equal(1, GraphWorkflowDocuments.Resolve(steeredEvent.DetailJson, "attempt")?.GetInt32() ?? 0);
        var steerMessage = (await MessagesAsync(harness.Services, conversationId)).Single(message => message.MessageId == GraphWorkflowChatIds.SteerMessage(operationId, runId, "analyze"));
        AssertEx.Equal("user", steerMessage.Role);
        AssertEx.Equal("focus on the failing tests", steerMessage.Content);
    }

    /// <summary>The settle-before-apply race: the row settled after the steer committed and before a tick judged it.</summary>
    [Test]
    public async Task ASteerWhoseRowSettledFirst_IsRecordedIgnored_AndResetsNothing()
    {
        const string instructions = "steer-after-settle";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks });
        var (runId, _) = await StartRunningAsync(harness, instructions);
        using var steered = await PostSteerAsync(Host.Factory, runId, "analyze", Guid.NewGuid(), "too late");
        AssertEx.Equal(HttpStatusCode.Accepted, steered.StatusCode);

        // What the poll of the same tick would have written after the apply pass had already read the rows.
        await harness.TransitionNodeRunAsync(runId, "analyze", GraphWorkflowNodeRunStatus.Failed, GraphWorkflowFailureClass.ValidationFailed, "settled first");
        _ = await harness.AdvanceAsync(runId);

        var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, nodeRun.Status, "an ignored steer resets nothing.");
        AssertEx.Equal<bool?>(false, nodeRun.Steering[0].Applied);
        var trail = await harness.ReadEventTrailAsync(runId);
        var ignoredEvent = (await harness.ReadEventsAsync(runId)).Single(static entry => entry.EventType == GraphWorkflowEventTypes.NodeSteerIgnored);
        AssertEx.Equal("too late", GraphWorkflowDocuments.Resolve(ignoredEvent.DetailJson, "message")?.GetString());
        AssertEx.False(trail.Contains(GraphWorkflowEventTypes.NodeSteered, StringComparison.Ordinal), trail);
        await harness.AdvanceUntilAsync(runId, async () => GraphWorkflowStateMachine.IsTerminal((await harness.ReadRunAsync(runId)).Status), "the run never settled");
    }

    /// <summary>
    ///     The steer that commits after the terminalising tick's apply pass: the run is over before any tick judged it, so
    ///     the next tick (the steer's own signal) records it ignored instead of leaving it unjudged forever.
    /// </summary>
    [Test]
    public async Task ASteerLeftUnjudgedWhenTheRunEnded_IsRecordedIgnoredByTheNextTick()
    {
        const string instructions = "steer-after-run-ended";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks });
        var (runId, _) = await StartRunningAsync(harness, instructions);
        var invocationId = (await harness.ReadNodeRunAsync(runId, "analyze")).InvocationId
                           ?? throw new AssertionException("a running Agent row carries its invocation id.");
        using var steered = await PostSteerAsync(Host.Factory, runId, "analyze", Guid.NewGuid(), "too late for the run");
        AssertEx.Equal(HttpStatusCode.Accepted, steered.StatusCode);

        // The state that tick leaves behind: its poll settled the row and its recompute ended the run, after the apply pass had read the rows.
        await harness.TransitionNodeRunAsync(runId, "analyze", GraphWorkflowNodeRunStatus.Succeeded);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            _ = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>()
                           .TransitionRunAsync(new TransitionGraphWorkflowRunCommand { RunId = runId, ExpectedVersion = GraphWorkflowVersions.Any, TargetStatus = GraphWorkflowRunStatus.Completed });
        }

        AssertEx.Equal(1, await harness.AdvanceAsync(runId), "the terminal tick judged the one steer.");
        AssertEx.Equal(0, await harness.AdvanceAsync(runId), "and has nothing left to judge.");

        AssertEx.Equal<bool?>(false, (await harness.ReadNodeRunAsync(runId, "analyze")).Steering[0].Applied);
        var trail = await harness.ReadEventTrailAsync(runId);
        AssertEx.Contains(trail, GraphWorkflowEventTypes.NodeSteerIgnored);
        AssertEx.False(trail.Contains(GraphWorkflowEventTypes.NodeSteered, StringComparison.Ordinal), trail);

        // Nothing ticks a terminal run's lane again, so the parked turn is released here or it holds the shared invocation slot.
        harness.Invocations.Cancel(invocationId);
    }

    private async Task<(Guid RunId, Guid ConversationId)> StartRunningAsync(GraphWorkflowHarness harness, string instructions)
    {
        var definitionId = await harness.SeedDefinitionAsync(ChatAgentGraph(instructions));
        var conversationId = await CreateConversationAsync(harness.Services);
        using var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "please analyze"));
        AssertEx.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var body = await ReadJsonAsync(start);
        var runId = body.RootElement.GetProperty("runId").GetGuid();
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "analyze")).Status == GraphWorkflowNodeRunStatus.Running,
            "the agent never started running");
        return (runId, conversationId);
    }
}
