namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies <c>AddListOrderIndexes</c> by the planner's verdict: the paged lists no longer sort their whole table,
///     and per-conversation log reads and deletes no longer scan it.
/// </summary>
/// <remarks>
///     The statements copy what the readers send (EF's SQL with the select list shortened to <c>*</c>, still
///     non-covering, or the raw SQL as written). Bound values are literals because EXPLAIN here takes no parameters;
///     none enters the plan, except the knowledge filter's, whose <c>IS NULL OR</c> form is why that index leads with time.
///     A reader that changes shape moves its copy.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AddListOrderIndexesMigrationTests
{
    private const string PreviousMigration = "20261113121004_AddConversationSamplingOptions";
    private const string ReceivedIndex = "ix_integration_executions_received";
    private const string ConversationIndex = "ix_agent_execution_logs_conversation_created";
    private const string KnowledgeIndex = "ix_knowledge_documents_created";
    private const string FireTimeIndex = "ix_scheduled_job_runs_fire_time";
    private const string AnyId = "'00000000-0000-0000-0000-000000000001'";

    // IntegrationExecutionStore.ListAsync, unfiltered.
    private const string IntegrationListQuery = """
                                                SELECT "i".* FROM "integration_executions" AS "i"
                                                ORDER BY "i"."received_at_utc" DESC, "i"."id" DESC
                                                LIMIT 50 OFFSET 0
                                                """;

    // ScheduledJobRunStore.ListAsync, unfiltered.
    private const string ScheduledRunListQuery = """
                                                 SELECT "s".* FROM "scheduled_job_runs" AS "s"
                                                 ORDER BY "s"."actual_fire_time_utc" DESC, "s"."id" DESC
                                                 LIMIT 50 OFFSET 0
                                                 """;

    // KnowledgeDocumentCatalogService.ListCoreAsync; the filter values are bound, as the service binds them.
    private const string KnowledgeListQuery = """
                                              SELECT * FROM knowledge_documents
                                              WHERE ($collection_id IS NULL OR collection_id = $collection_id)
                                                AND ($source_kind IS NULL OR source_kind = $source_kind)
                                                AND ($source_id IS NULL OR source_id = $source_id)
                                              ORDER BY created_at_utc DESC, document_id DESC
                                              LIMIT 50 OFFSET 0
                                              """;

    // AgentExecutionLogStore.ListRunEnvelopesAsync with a conversation.
    private const string EnvelopeListQuery = $"""
                                              SELECT "a".* FROM "agent_execution_logs" AS "a"
                                              WHERE "a"."record_kind" = 1 AND "a"."conversation_id" = {AnyId}
                                              ORDER BY "a"."created_at_utc" DESC, "a"."id" DESC
                                              LIMIT 50 OFFSET 0
                                              """;

    private const string ConversationPurgeDelete = $"DELETE FROM agent_execution_logs WHERE conversation_id = {AnyId}";

    private const string MessageDelete = $"DELETE FROM agent_execution_logs WHERE message_id = {AnyId} AND conversation_id = {AnyId}";

    [Test]
    public async Task MigrateToHead_CreatesEveryIndexInTheDeclaredColumnOrder()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-indexes.sqlite");

        AssertEx.True(await probe.IndexExistsAsync("integration_executions", ReceivedIndex, unique: false, "received_at_utc", "id"),
            $"{ReceivedIndex} must cover (received_at_utc, id), in that order.");
        AssertEx.True(await probe.IndexExistsAsync("agent_execution_logs", ConversationIndex, unique: false, "conversation_id", "created_at_utc"),
            $"{ConversationIndex} must cover (conversation_id, created_at_utc), in that order.");
        AssertEx.True(await probe.IndexExistsAsync("knowledge_documents", KnowledgeIndex, unique: false, "created_at_utc", "document_id"),
            $"{KnowledgeIndex} must cover (created_at_utc, document_id), in that order.");
        AssertEx.True(await probe.IndexExistsAsync("scheduled_job_runs", FireTimeIndex, unique: false, "actual_fire_time_utc", "id"),
            $"{FireTimeIndex} must cover (actual_fire_time_utc, id), in that order.");
    }

    [Test]
    public async Task UnfilteredIntegrationList_WalksTheIndexWithNoSort()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-integration-plan.sqlite");

        AssertWalksInOrder(await probe.QueryPlanAsync(IntegrationListQuery), ReceivedIndex, "the unfiltered integration-executions page");
    }

    [Test]
    public async Task UnfilteredScheduledRunList_WalksTheIndexWithNoSort()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-scheduled-plan.sqlite");

        AssertWalksInOrder(await probe.QueryPlanAsync(ScheduledRunListQuery), FireTimeIndex, "the unfiltered scheduler run history");
    }

    [Test]
    [Arguments(null)]
    [Arguments("default")]
    public async Task KnowledgeList_WithOrWithoutACollection_WalksTheIndexWithNoSort(string? collectionId)
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-knowledge-plan.sqlite");

        var plan = await probe.QueryPlanAsync(KnowledgeListQuery, command =>
        {
            command.Parameters.AddWithValue("$collection_id", (object?)collectionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$source_kind", DBNull.Value);
            command.Parameters.AddWithValue("$source_id", DBNull.Value);
        });

        AssertWalksInOrder(plan, KnowledgeIndex, $"the knowledge document list (collection {collectionId ?? "none"})");
    }

    [Test]
    public async Task EnvelopeListByConversation_SeeksTheConversation()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-envelope-plan.sqlite");

        AssertSeeks(await probe.QueryPlanAsync(EnvelopeListQuery), "the per-conversation envelope list");
    }

    [Test]
    public async Task ConversationPurgeDelete_SeeksTheConversation()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-purge-plan.sqlite");

        AssertSeeks(await probe.QueryPlanAsync(ConversationPurgeDelete), "the conversation purge delete");
    }

    [Test]
    public async Task MessageDelete_SeeksTheConversation()
    {
        // No record_kind term, so the partial envelope index cannot serve it; the conversation index does.
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-message-delete-plan.sqlite");

        AssertSeeks(await probe.QueryPlanAsync(MessageDelete), "the message delete");
    }

    [Test]
    public async Task RollingBackOneStep_DropsEveryIndex()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("list-order-indexes-rollback.sqlite");
        await probe.MigrateToAsync(PreviousMigration);

        AssertEx.False(await probe.IndexExistsAsync("integration_executions", ReceivedIndex, unique: false), $"Rolling back must drop {ReceivedIndex}.");
        AssertEx.False(await probe.IndexExistsAsync("agent_execution_logs", ConversationIndex, unique: false), $"Rolling back must drop {ConversationIndex}.");
        AssertEx.False(await probe.IndexExistsAsync("knowledge_documents", KnowledgeIndex, unique: false), $"Rolling back must drop {KnowledgeIndex}.");
        AssertEx.False(await probe.IndexExistsAsync("scheduled_job_runs", FireTimeIndex, unique: false), $"Rolling back must drop {FireTimeIndex}.");
    }

    private static void AssertWalksInOrder(IReadOnlyList<string> plan, string indexName, string what)
    {
        AssertUses(plan, indexName, what);
        AssertEx.False(plan.Any(static step => step.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
            $"{what} must be read in index order, with no sort of the whole table. Plan was: {Render(plan)}");
    }

    private static void AssertSeeks(IReadOnlyList<string> plan, string what)
    {
        AssertUses(plan, ConversationIndex, what);
        AssertEx.True(plan.Any(static step => step.StartsWith("SEARCH", StringComparison.Ordinal) && step.Contains("conversation_id=?", StringComparison.Ordinal)),
            $"{what} must seek by conversation_id, not scan agent_execution_logs. Plan was: {Render(plan)}");
    }

    private static void AssertUses(IReadOnlyList<string> plan, string indexName, string what) =>
        AssertEx.True(plan.Any(step => step.Contains(indexName, StringComparison.Ordinal)),
            $"The planner must drive {what} off {indexName}. Plan was: {Render(plan)}");

    private static string Render(IReadOnlyList<string> plan) => string.Join(" | ", plan);
}
