namespace XE_Local_AI_Engine.Tests.Endpoints.GraphWorkflows.V1;

using System.Net;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;
using XE_Local_AI_Engine.Tests.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;
using static XE_Local_AI_Engine.Tests.GraphWorkflows.GraphWorkflowChatTestSupport;

/// <summary>
///     The steer route's contract over a running chat-bound Agent: 202 and the persisted user message, idempotency on
///     the operation id, the per-node cap, and every refusal leaving no message behind.
/// </summary>
/// <remarks>
///     No tick runs between the steers, so each one is still unjudged intent when the next arrives. Serial: every test
///     parks a turn on the host's one invocation slot, and in parallel they starve each other's agents.
/// </remarks>
[Category(TestCategories.Integration)]
[NotInParallel("SteerEndpointLease")]
public sealed class GraphWorkflowSteerEndpointTests
{
    [ClassDataSource<GraphWorkflowAgentHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowAgentHostFixture Host { get; init; }

    [Test]
    public async Task Steer_ReplaysTheSameId_RefusesItWithAnotherMessage_AcceptsNewIds_AndRefusesPastTheCap()
    {
        const string instructions = "steer-endpoint-cap";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks });
        var (runId, conversationId) = await StartRunningAsync(harness, instructions);
        var first = Guid.NewGuid();

        using (var accepted = await PostSteerAsync(Host.Factory, runId, "analyze", first, "one"))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var body = await ReadJsonAsync(accepted);
            AssertEx.Equal(runId, body.RootElement.GetProperty("run").GetProperty("id").GetGuid(), "202 answers with the run detail.");
        }

        using (var replay = await PostSteerAsync(Host.Factory, runId, "analyze", first, "one"))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, replay.StatusCode, "the same operation id is the same steer.");
        }

        using (var conflict = await PostSteerAsync(Host.Factory, runId, "analyze", first, "a different message"))
        {
            AssertEx.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(conflict));
        }

        for (var steer = 2; steer <= 5; steer++)
        {
            using var more = await PostSteerAsync(Host.Factory, runId, "analyze", Guid.NewGuid(), $"steer {steer}");
            AssertEx.Equal(HttpStatusCode.Accepted, more.StatusCode, "a new operation id is a new steer, up to the cap.");
        }

        var overCap = Guid.NewGuid();
        using (var refused = await PostSteerAsync(Host.Factory, runId, "analyze", overCap, "one too many"))
        {
            AssertEx.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            AssertEx.Equal("GraphWorkflowSteerLimitReached", await ConflictTypeAsync(refused), "the cap is its own discriminator, apart from a settled row.");
        }

        var nodeRun = await harness.ReadNodeRunAsync(runId, "analyze");
        AssertEx.Equal(5, nodeRun.Steering.Count, "MaxSteersPerNode defaults to five.");
        AssertEx.Equal(1, nodeRun.Attempt);
        var messages = await MessagesAsync(harness.Services, conversationId);
        AssertEx.Equal("one", messages.Single(message => message.MessageId == GraphWorkflowChatIds.SteerMessage(first, runId, "analyze")).Content, "the replay wrote nothing new.");
        AssertEx.False(messages.Any(message => message.MessageId == GraphWorkflowChatIds.SteerMessage(overCap, runId, "analyze")), "a refused steer leaves no message behind.");
        using (var node = await GetNodeRunAsync(runId, "analyze"))
        {
            using var body = await ReadJsonAsync(node);
            var steering = body.RootElement.GetProperty("steering");
            AssertEx.Equal(5, steering.GetArrayLength());
            AssertEx.Equal("one", steering[0].GetProperty("message").GetString());
            AssertEx.False(steering[0].TryGetProperty("steeredBySubject", out _), "who steered stays server-side.");
        }

        await CancelAndDrainAsync(harness, runId);
    }

    [Test]
    public async Task Steer_RefusesANonAgentNode_ATerminalRun_AnUnboundRun_AnUnknownRun_AndABlankMessage()
    {
        const string instructions = "steer-endpoint-refusals";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks });
        var (runId, conversationId) = await StartRunningAsync(harness, instructions);

        using (var start = await PostSteerAsync(Host.Factory, runId, "start", Guid.NewGuid(), "steer the start"))
        {
            AssertEx.Equal(HttpStatusCode.BadRequest, start.StatusCode, "only an Agent or LLM call node takes a steer.");
        }

        using (var blank = await PostSteerAsync(Host.Factory, runId, "analyze", Guid.NewGuid(), "   "))
        {
            AssertEx.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        }

        using (var unknown = await PostSteerAsync(Host.Factory, Guid.NewGuid(), "analyze", Guid.NewGuid(), "nobody home"))
        {
            AssertEx.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var unknownNode = await PostSteerAsync(Host.Factory, runId, "no-such-node", Guid.NewGuid(), "steer a ghost"))
        {
            AssertEx.Equal(HttpStatusCode.NotFound, unknownNode.StatusCode);
        }

        AssertEx.Equal(1, (await MessagesAsync(harness.Services, conversationId)).Count, "only the trigger message: no refused steer wrote one.");

        await CancelAndDrainAsync(harness, runId);
        var late = Guid.NewGuid();
        using (var terminal = await PostSteerAsync(Host.Factory, runId, "analyze", late, "after the end"))
        {
            AssertEx.Equal(HttpStatusCode.Conflict, terminal.StatusCode, "a terminal run cannot be steered.");
            AssertEx.Equal("GraphWorkflowRunConflict", await ConflictTypeAsync(terminal));
        }

        AssertEx.False((await MessagesAsync(harness.Services, conversationId)).Any(message => message.MessageId == GraphWorkflowChatIds.SteerMessage(late, runId, "analyze")),
            "the refused steer's message was rolled back.");

        var unboundRunId = await harness.StartRunAsync(GraphWorkflowGraphs.StartAgentEnd);
        using var unbound = await PostSteerAsync(Host.Factory, unboundRunId, "analyze", Guid.NewGuid(), "no conversation");
        AssertEx.Equal(HttpStatusCode.BadRequest, unbound.StatusCode, "a Standard run is not bound to a conversation, so it has nowhere to record a steer.");
    }

    /// <summary>Idempotency is per node run, so one operation id on two nodes is two steers, each with its own chat message.</summary>
    [Test]
    public async Task Steer_TheSameOperationIdOnTwoNodes_IsTwoSteersWithTwoMessages()
    {
        const string left = "steer-two-nodes-left";
        const string right = "steer-two-nodes-right";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(left, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks });
        harness.Invocations.Script(right, new GraphWorkflowScriptedTurn { Outcome = GraphWorkflowTurnOutcome.Parks });
        var definitionId = await harness.SeedDefinitionAsync(TwoAgentGraph(left, right));
        var conversationId = await CreateConversationAsync(harness.Services);
        using var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "fan out"));
        using var started = await ReadJsonAsync(start);
        var runId = started.RootElement.GetProperty("runId").GetGuid();
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunsAsync(runId)).Count(static row => row.Status is GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running) == 2,
            "both agents never reached the lane");
        var operationId = Guid.NewGuid();

        using (var first = await PostSteerAsync(Host.Factory, runId, "left", operationId, "left, go deeper"))
        using (var second = await PostSteerAsync(Host.Factory, runId, "right", operationId, "right, go wider"))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, first.StatusCode);
            AssertEx.Equal(HttpStatusCode.Accepted, second.StatusCode);
        }

        AssertEx.Equal(1, (await harness.ReadNodeRunAsync(runId, "left")).Steering.Count);
        AssertEx.Equal(1, (await harness.ReadNodeRunAsync(runId, "right")).Steering.Count);
        var messages = await MessagesAsync(harness.Services, conversationId);
        AssertEx.Equal("left, go deeper", messages.Single(message => message.MessageId == GraphWorkflowChatIds.SteerMessage(operationId, runId, "left")).Content);
        AssertEx.Equal("right, go wider", messages.Single(message => message.MessageId == GraphWorkflowChatIds.SteerMessage(operationId, runId, "right")).Content);

        await CancelAndDrainAsync(harness, runId);
    }

    private static string TwoAgentGraph(string left, string right) =>
        $$"""
          {
            "schemaVersion": 1,
            "kind": "Chat",
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "left", "kind": "Agent", "config": { "instructions": "{{left}}" } },
              { "key": "right", "kind": "Agent", "config": { "instructions": "{{right}}" } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed", "publishToChat": false } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "left" },
              { "key": "e2", "from": "start", "to": "right" },
              { "key": "e3", "from": "left", "to": "done" },
              { "key": "e4", "from": "right", "to": "done" }
            ]
          }
          """;

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

    private async Task<HttpResponseMessage> GetNodeRunAsync(Guid runId, string nodeKey)
    {
        using var client = Host.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Root}/runs/{runId}/nodes/{nodeKey}");
        Host.Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }

    private static async Task CancelAndDrainAsync(GraphWorkflowHarness harness, Guid runId)
    {
        await harness.CancelAsync(runId);
        await harness.AdvanceUntilAsync(runId, async () => (await harness.ReadRunAsync(runId)).Status == GraphWorkflowRunStatus.Cancelled, "the run never cancelled");
    }
}
