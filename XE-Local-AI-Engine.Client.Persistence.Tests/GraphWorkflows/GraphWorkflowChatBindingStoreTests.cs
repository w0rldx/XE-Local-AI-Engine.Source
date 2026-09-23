namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The chat binding on a run: the conversation foreign key that nulls on delete, the partial unique index that allows
///     one LIVE run per conversation, the parked-run concurrency rule and the publish outbox stamp.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowChatBindingStoreTests
{
    [Test]
    public async Task StartRun_BoundToAConversation_PersistsTheBindingAndListsItNewestFirst()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        var triggerMessageId = Guid.NewGuid();

        var run = await store.StartRunAsync(StartCommand(definition, conversationId, triggerMessageId));

        AssertEx.Equal(conversationId, run.ConversationId);
        AssertEx.Equal(triggerMessageId, run.TriggerMessageId);
        var listed = await store.ListRunsByConversationAsync(conversationId, limit: 10);
        AssertEx.Equal(expected: 1, listed.Count);
        AssertEx.Equal(run.Id, listed[0].Id);
        AssertEx.Empty(await store.ListRunsByConversationAsync(Guid.NewGuid(), limit: 10), "a conversation with no runs lists none.");
    }

    /// <summary>The database, not the chat service, is what makes a second live run on one conversation impossible.</summary>
    [Test]
    public async Task StartRun_WhileTheConversationHasALiveRun_AnswersBusyAndWritesNoSecondRun()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        _ = await store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid()));

        _ = await AssertEx.ThrowsAsync<GraphWorkflowRunBusyException>(() => store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid())));

        AssertEx.Equal(expected: 1, await fixture.RawTableCountAsync("graph_workflow_runs"));
    }

    /// <summary>The index is PARTIAL: a terminal run no longer holds the conversation, so the next one starts.</summary>
    [Test]
    public async Task StartRun_AfterTheBoundRunTerminated_IsAdmittedByThePartialIndex()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        var first = await store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid()));
        _ = await store.TransitionRunAsync(new TransitionGraphWorkflowRunCommand
        {
            RunId = first.Id,
            ExpectedVersion = GraphWorkflowVersions.Any,
            TargetStatus = GraphWorkflowRunStatus.Cancelled,
            FailureClass = GraphWorkflowFailureClass.Cancelled
        });

        var second = await store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid()));

        AssertEx.Equal(expected: 2, (await store.ListRunsByConversationAsync(conversationId, limit: 10)).Count);
        AssertEx.Equal(second.Id, (await store.ListRunsByConversationAsync(conversationId, limit: 10))[0].Id, "newest first.");
    }

    /// <summary>ON DELETE SET NULL: a deleted conversation leaves its runs standing as unbound history.</summary>
    [Test]
    public async Task DeletingTheConversation_NullsTheRunsBinding_AndKeepsTheRun()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        var run = await store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid()));

        await fixture.RawExecuteAsync("PRAGMA foreign_keys = ON; DELETE FROM conversations WHERE conversation_id = $id;",
            command => command.Parameters.AddWithValue("$id", conversationId.ToString().ToUpperInvariant()));

        AssertEx.Equal(expected: 1, await fixture.RawTableCountAsync("graph_workflow_runs"), "the run is history, not a cascade victim.");
        AssertEx.Null((await store.GetRunAsync(run.Id)).ConversationId);
    }

    /// <summary>The conversation purge helper unbinds a run itself — no reliance on the connection's foreign-key pragma.</summary>
    [Test]
    public async Task TheFootprintPurge_UnbindsTheRun_AndKeepsIt()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        var run = await store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid()));

        await ConversationFootprintPurge.DeleteAsync(context, conversationId, CancellationToken.None);

        AssertEx.Equal(expected: 1, await fixture.RawTableCountAsync("graph_workflow_runs"));
        AssertEx.Null((await store.GetRunAsync(run.Id)).ConversationId);
    }

    /// <summary>
    ///     A parked run releases its concurrency slot: every run here waits on a person and runs nothing, so none of them
    ///     counts, while a run with work in flight and a draining one still do.
    /// </summary>
    [Test]
    public async Task CountActiveRuns_SkipsParkedRuns_AndCountsRunningAndCancellingOnes()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var definitionId = Guid.NewGuid();
        for (var parked = 0; parked < 4; parked++)
        {
            var runId = await GraphWorkflowTestFixture.SeedRunAsync(context, definitionId, GraphWorkflowRunStatus.WaitingForApproval);
            _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "ask", GraphWorkflowNodeKind.ChatInput, GraphWorkflowNodeRunStatus.WaitingForApproval);
            _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "later", GraphWorkflowNodeKind.Agent);
        }

        var store = GraphWorkflowTestFixture.StoreFor(context);
        AssertEx.Equal(expected: 0, await store.CountActiveRunsAsync(probeLimit: 10), "four parked runs hold no slot.");

        var busyParked = await GraphWorkflowTestFixture.SeedRunAsync(context, definitionId, GraphWorkflowRunStatus.WaitingForApproval);
        _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, busyParked, "ask", GraphWorkflowNodeKind.Pause, GraphWorkflowNodeRunStatus.WaitingForApproval);
        _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, busyParked, "branch", GraphWorkflowNodeKind.Agent, GraphWorkflowNodeRunStatus.Running);
        var running = await GraphWorkflowTestFixture.SeedRunAsync(context, definitionId);
        _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, running, "agent", GraphWorkflowNodeKind.Agent, GraphWorkflowNodeRunStatus.Pending);
        var cancelling = await GraphWorkflowTestFixture.SeedRunAsync(context, definitionId, GraphWorkflowRunStatus.Cancelling);
        _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, cancelling, "ask", GraphWorkflowNodeKind.Pause, GraphWorkflowNodeRunStatus.WaitingForApproval);

        AssertEx.Equal(expected: 3, await store.CountActiveRunsAsync(probeLimit: 10),
            "a waiting run with a branch still running, a running run between ticks, and a draining run each hold a slot.");
    }

    /// <summary>The outbox stamp is a compare-and-set: a second stamp of the same row writes nothing and appends no second event.</summary>
    [Test]
    public async Task MarkNodeRunPublished_StampsOnce_AndDropsTheRowFromTheCandidates()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var runId = await GraphWorkflowTestFixture.SeedRunAsync(context, Guid.NewGuid());
        var nodeRunId = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "agent", GraphWorkflowNodeKind.Agent, GraphWorkflowNodeRunStatus.Succeeded);
        _ = await GraphWorkflowTestFixture.SeedNodeRunAsync(context, runId, "later", GraphWorkflowNodeKind.Agent, GraphWorkflowNodeRunStatus.Running);
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var messageId = Guid.NewGuid();

        var candidates = await store.ListUnpublishedNodeRunsAsync(runId);
        AssertEx.Equal(expected: 1, candidates.Count, "only a Succeeded row is a candidate.");
        AssertEx.Equal(nodeRunId, candidates[0].Id);
        AssertEx.NotNull(await store.MarkNodeRunPublishedAsync(runId, nodeRunId, messageId));
        AssertEx.Null(await store.MarkNodeRunPublishedAsync(runId, nodeRunId, Guid.NewGuid()), "a second stamp matches no row.");

        AssertEx.Empty(await store.ListUnpublishedNodeRunsAsync(runId));
        AssertEx.Equal(messageId, (await store.GetNodeRunAsync(runId, "agent")).PublishedMessageId);
        var publishedEvents = (await store.ListEventsAsync(runId)).Where(static entry => entry.EventType == GraphWorkflowEventTypes.NodePublished).ToList();
        AssertEx.Equal(expected: 1, publishedEvents.Count);
        var published = publishedEvents[0];
        AssertEx.Equal("agent", published.NodeKey);
        AssertEx.True(published.DetailJson!.Contains(messageId.ToString(), StringComparison.OrdinalIgnoreCase), "the event names the message it published.");
    }

    [Test]
    public async Task FindConversationDecision_FindsAnAnswerOnlyAmongTheConversationsRuns()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        var run = await store.StartRunAsync(StartCommand(definition, conversationId, Guid.NewGuid()));
        var operationId = Guid.NewGuid();
        await fixture.RawExecuteAsync("UPDATE graph_workflow_node_runs SET decision_operation_id = $op WHERE node_key = 'start';",
            command => command.Parameters.AddWithValue("$op", operationId.ToString().ToUpperInvariant()));

        AssertEx.Equal(run.Id, (await store.FindConversationDecisionAsync(conversationId, operationId))?.RunId);
        AssertEx.Null(await store.FindConversationDecisionAsync(Guid.NewGuid(), operationId), "another conversation never sees this answer.");
    }

    private static async Task<Guid> SeedConversationAsync(NodeChatDbContext context)
    {
        var conversationId = Guid.NewGuid();
        context.Conversations.Add(new NodeConversation { ConversationId = conversationId, UserId = "node", CreatedAtUtc = 1, LastSeenUtc = 1 });
        _ = await context.SaveChangesAsync();
        return conversationId;
    }

    private static StartGraphWorkflowRunCommand StartCommand(GraphWorkflowDefinitionSnapshot definition, Guid conversationId, Guid triggerMessageId) =>
        new()
        {
            RunId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            DefinitionId = definition.Id,
            DefinitionVersion = definition.Version,
            GraphHash = definition.GraphHash,
            GraphJson = definition.GraphJson,
            InputJson = null,
            NodeRuns = [.. new[] { "start", "done" }.Select(static key => new GraphWorkflowNodeRunSeed { NodeRunId = Guid.NewGuid(), NodeKey = key, Kind = GraphWorkflowNodeKind.Agent })],
            ConversationId = conversationId,
            TriggerMessageId = triggerMessageId
        };
}
