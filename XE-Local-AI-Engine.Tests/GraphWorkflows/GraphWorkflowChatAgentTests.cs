namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Net;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;
using static GraphWorkflowChatTestSupport;

/// <summary>A chat-bound run whose Agent actually runs on the fake lane: busy while it runs, steerable in the run list, published once.</summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowChatAgentTests
{
    [ClassDataSource<GraphWorkflowAgentHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowAgentHostFixture Host { get; init; }

    [Test]
    public async Task Send_WhileTheAgentIsRunning_IsBusy_AndTheRunListNamesItSteerable()
    {
        const string instructions = "chat-agent-still-running";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn
        {
            Outcome = GraphWorkflowTurnOutcome.Parks
        });
        var definitionId = await harness.SeedDefinitionAsync(ChatAgentGraph(instructions));
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAsync(definitionId, conversationId);
        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "analyze")).Status == GraphWorkflowNodeRunStatus.Running,
            "the agent never started running");

        using var busy = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "not now"));
        using var listed = await GetRunsAsync(Host.Factory, conversationId);

        AssertEx.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        AssertEx.Equal("GraphWorkflowRunBusy", await ConflictTypeAsync(busy));
        using var body = await ReadJsonAsync(listed);
        var run = body.RootElement.GetProperty("runs")[0];
        AssertEx.Equal("analyze", run.GetProperty("steerable").GetProperty("nodeKey").GetString());
        AssertEx.Equal(JsonValueKind.Null, run.GetProperty("pendingInput").ValueKind);

        // The parked turn ends on the cancel; nothing may stay running on the shared host.
        await harness.CancelAsync(runId);
        await harness.AdvanceUntilAsync(runId, async () => (await harness.ReadRunAsync(runId)).Status == GraphWorkflowRunStatus.Cancelled, "the run never cancelled");
    }

    [Test]
    public async Task APublishingAgent_PostsItsAnswerOnce_AttributedToItsNodeAndModel()
    {
        const string instructions = "chat-agent-publishes";
        await using var harness = new GraphWorkflowHarness(Host);
        harness.Invocations.Script(instructions, new GraphWorkflowScriptedTurn
        {
            Text = "the analysis"
        });
        var definitionId = await harness.SeedDefinitionAsync(ChatAgentGraph(instructions));
        var conversationId = await CreateConversationAsync(harness.Services);
        var runId = await StartAsync(definitionId, conversationId);

        await harness.AdvanceUntilAsync(runId, async () => (await harness.ReadRunAsync(runId)).Status == GraphWorkflowRunStatus.Completed, "the run never completed");
        _ = await harness.AdvanceAsync(runId);

        var assistant = (await MessagesAsync(harness.Services, conversationId)).Where(static message => message.Role == "assistant").ToList();
        AssertEx.Equal(1, assistant.Count, "one message per succeeded attempt, and the End publishes nothing here.");
        AssertEx.Equal("the analysis", assistant[0].Content);
        AssertEx.Equal("Analyst", assistant[0].AgentName);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, assistant[0].Status);
        var output = (await harness.ReadNodeRunAsync(runId, "analyze")).OutputJson;
        AssertEx.Equal<string?>(GraphWorkflowDocuments.Resolve(output, "output.usage.model")?.GetString(), assistant[0].Model, "the model comes from the node's usage.");
        AssertEx.Equal(1, (await harness.ReadEventsAsync(runId)).Count(static entry => entry.EventType == GraphWorkflowEventTypes.NodePublished));
    }

    private async Task<Guid> StartAsync(Guid definitionId, Guid conversationId)
    {
        using var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "please analyze"));
        AssertEx.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var body = await ReadJsonAsync(start);
        return body.RootElement.GetProperty("runId").GetGuid();
    }
}
