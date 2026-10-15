namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     What <c>run.previous</c> is built from: the newest Completed run of a definition, and the encrypted column a new run
///     pins it into.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowPreviousRunStoreTests
{
    [Test]
    public async Task FindLatestCompletedRun_TakesTheNewestCompletedRun_AndIgnoresEveryOtherStatusAndDefinition()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store, "Mine");
        var other = await GraphWorkflowTestFixture.SeedDefinitionAsync(store, "Other");

        _ = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 100, "\"older\"");
        var newest = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 200, "\"newest\"");
        _ = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Failed, completedAtUtc: 300, outputJson: null);
        _ = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Cancelled, completedAtUtc: 400, outputJson: null);
        _ = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Running, completedAtUtc: null, outputJson: null);
        _ = await SeedRunAsync(context, other.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 500, "\"other definition\"");

        var found = AssertEx.NotNull(await store.FindLatestCompletedRunAsync(definition.Id, conversationId: null));

        AssertEx.Equal(newest, found.Id, "the newest Completed run wins over failed, cancelled, running and other-definition rows completed later.");
        AssertEx.Equal("\"newest\"", found.OutputJson, "and it reads back decrypted.");
    }

    [Test]
    public async Task FindLatestCompletedRun_WithNoCompletedRun_AnswersNull()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        _ = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Failed, completedAtUtc: 100, outputJson: null);

        AssertEx.Null(await store.FindLatestCompletedRunAsync(definition.Id, conversationId: null), "a failed run is not a previous answer.");
        AssertEx.Null(await store.FindLatestCompletedRunAsync(Guid.NewGuid(), conversationId: null), "an unknown definition has no previous run, and is not an error.");
    }

    /// <summary>
    ///     A chat run's result may quote its own conversation, so the previous run never crosses one: a bound start sees only
    ///     its conversation's runs, and an unbound start sees only unbound runs.
    /// </summary>
    [Test]
    public async Task FindLatestCompletedRun_StaysInsideTheNewRunsConversation()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationA = await SeedConversationAsync(context);
        var conversationB = await SeedConversationAsync(context);

        var inB = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 100, "\"b\"", conversationB);
        var inA = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 200, "\"a\"", conversationA);
        var unbound = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 300, "\"page\"");

        AssertEx.Equal(inB, (await store.FindLatestCompletedRunAsync(definition.Id, conversationB))?.Id, "B never sees A's newer run, nor the unbound one.");
        AssertEx.Equal(inA, (await store.FindLatestCompletedRunAsync(definition.Id, conversationA))?.Id);
        AssertEx.Equal(unbound, (await store.FindLatestCompletedRunAsync(definition.Id, conversationId: null))?.Id, "an unbound start sees unbound runs only.");
        AssertEx.Null(await store.FindLatestCompletedRunAsync(definition.Id, Guid.NewGuid()), "a conversation with no runs has no previous run.");
    }

    /// <summary>
    ///     A purge nulls a chat run's conversation but keeps the run as history. Its result still quotes the deleted chat, so an
    ///     unbound start must not adopt it; the kept trigger message is what says the run was chat-started.
    /// </summary>
    [Test]
    public async Task FindLatestCompletedRun_ForAnUnboundStart_IgnoresARunWhoseConversationWasPurged()
    {
        using var fixture = new GraphWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = GraphWorkflowTestFixture.StoreFor(context);
        var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
        var conversationId = await SeedConversationAsync(context);
        var chatRun = await SeedRunAsync(context, definition.Id, GraphWorkflowRunStatus.Completed, completedAtUtc: 100, "\"chat\"", conversationId);

        await ConversationFootprintPurge.DeleteAsync(context, conversationId, CancellationToken.None);

        AssertEx.Null((await store.GetRunAsync(chatRun)).ConversationId, "precondition: the purge unbound the run and kept it.");
        AssertEx.Null(await store.FindLatestCompletedRunAsync(definition.Id, conversationId: null));
    }

    /// <summary>The pinned previous result is a document column like the output it copies: encrypted at rest, decrypted on read.</summary>
    [Test]
    public async Task StartRun_PinsThePreviousJson_EncryptedAtRest()
    {
        using var fixture = new GraphWorkflowTestFixture();
        var needle = "PREVIOUSJSON-" + Guid.NewGuid().ToString("N");
        var previousJson = $$"""{"runId":"{{Guid.NewGuid()}}","completedAtUtc":"2026-10-12T10:00:00Z","output":"{{needle}}"}""";
        Guid runId;

        await using (var context = await fixture.CreateSchemaAsync())
        {
            var store = GraphWorkflowTestFixture.StoreFor(context);
            var definition = await GraphWorkflowTestFixture.SeedDefinitionAsync(store);
            runId = (await store.StartRunAsync(new StartGraphWorkflowRunCommand
            {
                RunId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                DefinitionId = definition.Id,
                DefinitionVersion = definition.Version,
                GraphHash = definition.GraphHash,
                GraphJson = definition.GraphJson,
                InputJson = null,
                PreviousJson = previousJson,
                TriggerSource = GraphWorkflowTriggerSource.Manual,
                NodeRuns = []
            })).Id;
        }

        await using (var readContext = fixture.CreateContext())
        {
            AssertEx.Equal(previousJson, (await GraphWorkflowTestFixture.StoreFor(readContext).GetRunAsync(runId)).PreviousJson, "through a fresh context.");
        }

        var fileBytes = await SqliteFileProbe.ReadAllBytesAsync(fixture.DatabasePath);
        AssertEx.False(fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0, "previous_json must not reach the file as plaintext.");
    }

    private static async Task<Guid> SeedRunAsync(NodeChatDbContext context,
        Guid definitionId,
        GraphWorkflowRunStatus status,
        long? completedAtUtc,
        string? outputJson,
        Guid? conversationId = null)
    {
        var run = new GraphWorkflowRun
        {
            Id = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            DefinitionId = definitionId,
            DefinitionVersion = 1,
            GraphHash = "hash-" + Guid.NewGuid().ToString("N"),
            Status = status,
            FailureClass = GraphWorkflowFailureClass.None,
            GraphJson = Encoding.UTF8.GetBytes(GraphWorkflowTestFixture.SampleGraph),
            OutputJson = outputJson is null ? null : Encoding.UTF8.GetBytes(outputJson),
            Seq = 0,
            Version = 1,
            CompletedAtUtc = completedAtUtc,
            CreatedAtUtc = 1,
            ConversationId = conversationId,
            TriggerMessageId = conversationId is null ? null : Guid.NewGuid()
        };
        _ = context.GraphWorkflowRuns.Add(run);
        _ = await context.SaveChangesAsync();
        return run.Id;
    }

    private static async Task<Guid> SeedConversationAsync(NodeChatDbContext context)
    {
        var conversationId = Guid.NewGuid();
        context.Conversations.Add(new NodeConversation
        {
            ConversationId = conversationId,
            UserId = "node",
            CreatedAtUtc = 1,
            LastSeenUtc = 1
        });
        _ = await context.SaveChangesAsync();
        return conversationId;
    }
}
