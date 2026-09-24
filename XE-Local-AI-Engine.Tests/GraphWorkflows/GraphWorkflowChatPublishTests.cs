namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;
using XE_Local_AI_Engine.Tests.Testing;
using static GraphWorkflowChatTestSupport;

/// <summary>
///     The binding's runtime half: the publish outbox pass (idempotent in either crash order, never failing a node), the
///     partial index behind a second live run, conversation delete, the parked-run concurrency rule and the chat-turn guard.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowChatPublishTests
{
    [ClassDataSource<GraphWorkflowHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowHostFixture Host { get; init; }

    /// <summary>The insert landed and the host died before the stamp: the next tick re-inserts nothing and only stamps.</summary>
    [Test]
    public async Task APublishWhoseInsertLandedBeforeACrash_EndsAsOneMessage()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (definitionId, conversationId, runId) = await ParkedBoundRunAsync(harness);
        var messageId = GraphWorkflowChatIds.PublishedMessage(runId, "done", attempt: 1);
        _ = await harness.Services.GetRequiredService<INodeChatPersistenceService>()
                         .InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
                         {
                             ConversationId = conversationId,
                             MessageId = messageId,
                             Role = "assistant",
                             Content = "written before the crash",
                             CreatedAtUtc = 2
                         });

        await AnswerAndFinishAsync(harness, definitionId, conversationId, runId);

        var assistant = (await MessagesAsync(harness.Services, conversationId)).Where(static message => message.Role == "assistant").ToList();
        AssertEx.Equal(1, assistant.Count);
        AssertEx.Equal(messageId, assistant[0].MessageId);
        AssertEx.Equal<Guid?>(messageId, (await harness.ReadNodeRunAsync(runId, "done")).PublishedMessageId);
        AssertEx.Equal(1, (await harness.ReadEventsAsync(runId)).Count(static entry => entry.EventType == GraphWorkflowEventTypes.NodePublished));
    }

    /// <summary>A pass over rows it already stamped finds no candidate: ticking again, or re-running the pass, writes nothing.</summary>
    [Test]
    public async Task ThePublishPassRunTwice_WritesOneMessage()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (definitionId, conversationId, runId) = await ParkedBoundRunAsync(harness);
        await AnswerAndFinishAsync(harness, definitionId, conversationId, runId);

        _ = await harness.AdvanceAsync(runId);
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>();
            var run = await store.GetRunAsync(runId);
            var (written, failed) = await harness.Services.GetRequiredService<GraphWorkflowChatPublisher>().PublishAsync(store, run, GraphWorkflowGraph.Parse(run.GraphJson), CancellationToken.None);
            AssertEx.Equal(0, written);
            AssertEx.Equal(0, failed);
        }

        AssertEx.Equal(1, (await MessagesAsync(harness.Services, conversationId)).Count(static message => message.Role == "assistant"));
    }

    /// <summary>A publish that fails holds the run's end back; once the cause clears, the retried publish lands and the run completes.</summary>
    [Test]
    public async Task APublishThatFailsOnce_IsRetriedBeforeTheRunCompletes()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (definitionId, conversationId, runId) = await ParkedBoundRunAsync(harness);
        var persistence = harness.Services.GetRequiredService<INodeChatPersistenceService>();
        var (elsewhere, squatted) = await SquatThePublishedIdAsync(harness, runId);
        using (var answer = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "postgres")))
        {
            AssertEx.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        }

        await harness.AdvanceUntilAsync(runId,
            async () => (await harness.ReadNodeRunAsync(runId, "done")).Status == GraphWorkflowNodeRunStatus.Succeeded,
            "the End never succeeded",
            maxTicks: 3);
        AssertEx.NotEqual(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status, "a failed publish holds the run's end back.");

        await persistence.DeleteMessageAsync(elsewhere, squatted);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.Equal<Guid?>(squatted, (await harness.ReadNodeRunAsync(runId, "done")).PublishedMessageId);
        AssertEx.Equal("postgres", (await MessagesAsync(harness.Services, conversationId)).Single(static message => message.Role == "assistant").Content);
    }

    /// <summary>A publish that keeps failing is given up after its bounded retries: the node stays Succeeded and the run completes.</summary>
    [Test]
    public async Task APublishThatKeepsFailing_GivesUp_AndNeverFailsTheNodeOrTheRun()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (definitionId, conversationId, runId) = await ParkedBoundRunAsync(harness);
        _ = await SquatThePublishedIdAsync(harness, runId);

        await AnswerAndFinishAsync(harness, definitionId, conversationId, runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        var done = await harness.ReadNodeRunAsync(runId, "done");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, done.Status);
        AssertEx.Null(done.PublishedMessageId, "nothing was published, so nothing is stamped.");
        AssertEx.Empty((await MessagesAsync(harness.Services, conversationId)).Where(static message => message.Role == "assistant"));
    }

    /// <summary>A published message carries its run envelope from the insert, so the restart reconcile never backfills it as a chat run.</summary>
    [Test]
    public async Task APublishedMessage_CarriesItsRunEnvelope()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (definitionId, conversationId, runId) = await ParkedBoundRunAsync(harness);
        await AnswerAndFinishAsync(harness, definitionId, conversationId, runId);
        var messageId = GraphWorkflowChatIds.PublishedMessage(runId, "done", attempt: 1);

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var envelopes = await db.Database
                                .SqlQuery<int>(
                                    $"SELECT COUNT(*) AS \"Value\" FROM agent_execution_logs WHERE message_id = {messageId} AND record_kind = {(int)AgentExecutionLogRecordKind.ChatRunEnvelope} AND success = 1")
                                .SingleAsync();

        AssertEx.Equal(1, envelopes);
    }

    [Test]
    public async Task AnUnboundRun_PublishesNothing()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var runId = await harness.StartRunAsync(ChatInputGraph());
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        _ = await harness.DecideAsync(runId, "ask", Guid.NewGuid(), GraphWorkflowDecisionKind.Answer, payloadJson: """{"text":"postgres"}""");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);

        AssertEx.Equal(GraphWorkflowRunStatus.Completed, (await harness.ReadRunAsync(runId)).Status);
        AssertEx.False((await harness.ReadEventTrailAsync(runId)).Contains("node.published", StringComparison.Ordinal));
    }

    /// <summary>The chat service checks the newest run first; the partial unique index is what holds when that check is raced.</summary>
    [Test]
    public async Task ASecondLiveRunOnOneConversation_LosesToThePartialIndex()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        var binding = new GraphWorkflowRunBinding
        {
            ConversationId = conversationId,
            TriggerMessageId = Guid.NewGuid()
        };
        await using var scope = harness.Services.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>();
        _ = await runs.StartAsync(definitionId, Guid.NewGuid(), inputJson: null, definitionVersion: null, binding);

        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunBusyException>(() => runs.StartAsync(definitionId, Guid.NewGuid(), inputJson: null, definitionVersion: null, binding));

        AssertEx.Equal(1, (await harness.ReadConversationRunsAsync(conversationId)).Count);
    }

    [Test]
    public async Task DeletingAndPurgingTheConversation_CancelsItsLiveRun_AndUnbindsIt()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (_, conversationId, runId) = await ParkedBoundRunAsync(harness);

        _ = await harness.Services.GetRequiredService<INodeChatPersistenceService>()
                         .DeleteConversationAsync(new NodeChatDeleteConversationRequest
                         {
                             ConversationId = conversationId,
                             DeletedAtUtc = 3,
                             PurgeImmediately = true
                         });

        var run = await harness.ReadRunAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, run.Status, "a run parked on a ChatInput has nobody left to answer it.");
        AssertEx.Null(run.ConversationId, "the purge nulled the binding through the foreign key.");
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelled, (await harness.ReadRunAsync(runId)).Status);
    }

    [Test]
    public async Task SoftDeletingTheConversation_CancelsItsLiveRun_AndKeepsTheBinding()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var (_, conversationId, runId) = await ParkedBoundRunAsync(harness);

        _ = await harness.Services.GetRequiredService<INodeChatPersistenceService>()
                         .DeleteConversationAsync(new NodeChatDeleteConversationRequest
                         {
                             ConversationId = conversationId,
                             DeletedAtUtc = 3
                         });

        var run = await harness.ReadRunAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.Cancelling, run.Status);
        AssertEx.Equal<Guid?>(conversationId, run.ConversationId, "a soft delete keeps the row, so the binding stands until the purge.");
    }

    /// <summary>Parked runs release their slot: with the cap at two and two runs parked on a Pause, a third start is admitted.</summary>
    [Test]
    public async Task ParkedRuns_DoNotHoldAConcurrencySlot()
    {
        // A private host: the cap counts runs across the whole database, and this test needs it at two.
        await using var harness = new GraphWorkflowHarness(("GraphWorkflows:MaxConcurrentRuns", "2"));
        for (var parkedRuns = 0; parkedRuns < 2; parkedRuns++)
        {
            var parked = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions);
            _ = await harness.AdvanceUntilQuiescentAsync(parked);
            AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(parked)).Status);
        }

        var third = await harness.StartRunAsync(GraphWorkflowGraphs.PauseTwoDecisions);
        _ = await harness.AdvanceAsync(third);

        AssertEx.Equal(GraphWorkflowRunStatus.Running, (await harness.ReadRunAsync(third)).Status, "two parked runs left both slots free.");
    }

    [Test]
    public async Task Send_WhileANormalChatReplyIsStreaming_IsBusy()
    {
        // A private host whose resume registry reports a live chat turn on every conversation — the one seam a streaming reply needs.
        var registry = Substitute.For<IInvocationResumeRegistry>();
        registry.TryGetLiveInvocationIdForConversation(Arg.Any<Guid>()).Returns(Guid.NewGuid());
        await using var harness = GraphWorkflowHarness.PrivateHost(services =>
        {
            services.RemoveAll<IInvocationResumeRegistry>();
            services.AddSingleton(registry);
        });
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);

        using var response = await PostMessageAsync(harness.Factory, conversationId, Body(definitionId, "hello"));

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertEx.Equal("GraphWorkflowRunBusy", await ConflictTypeAsync(response));
        AssertEx.Empty(await MessagesAsync(harness.Services, conversationId));
    }

    private async Task<(Guid DefinitionId, Guid ConversationId, Guid RunId)> ParkedBoundRunAsync(GraphWorkflowHarness harness)
    {
        var definitionId = await harness.SeedDefinitionAsync(ChatInputGraph());
        var conversationId = await CreateConversationAsync(harness.Services);
        using var start = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "hello"));
        AssertEx.Equal(HttpStatusCode.Accepted, start.StatusCode);
        using var body = await ReadJsonAsync(start);
        var runId = body.RootElement.GetProperty("runId").GetGuid();
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
        AssertEx.Equal(GraphWorkflowRunStatus.WaitingForApproval, (await harness.ReadRunAsync(runId)).Status);
        return (definitionId, conversationId, runId);
    }

    /// <summary>Takes the End's deterministic publish id in ANOTHER conversation, so every publish of it refuses until the squatter goes.</summary>
    private static async Task<(Guid Conversation, Guid MessageId)> SquatThePublishedIdAsync(GraphWorkflowHarness harness, Guid runId)
    {
        var elsewhere = await CreateConversationAsync(harness.Services);
        var messageId = GraphWorkflowChatIds.PublishedMessage(runId, "done", attempt: 1);
        _ = await harness.Services.GetRequiredService<INodeChatPersistenceService>()
                         .InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
                         {
                             ConversationId = elsewhere,
                             MessageId = messageId,
                             Role = "assistant",
                             Content = "squatter",
                             CreatedAtUtc = 2
                         });
        return (elsewhere, messageId);
    }

    private async Task AnswerAndFinishAsync(GraphWorkflowHarness harness, Guid definitionId, Guid conversationId, Guid runId)
    {
        using var answer = await PostMessageAsync(Host.Factory, conversationId, Body(definitionId, "postgres"));
        AssertEx.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        _ = await harness.AdvanceUntilQuiescentAsync(runId);
    }
}
