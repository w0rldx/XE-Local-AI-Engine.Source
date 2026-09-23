namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddChatWorkflowBinding</c>: the nullable conversation binding on runs (a foreign key that nulls on delete, a
///     partial unique index over the live statuses), the trigger message id, and the node runs' publish outbox stamp.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddChatWorkflowBindingMigrationTests
{
    private const string PreviousMigrationId = "20260923005438_AddChatWorkflowNodes";
    private const string ThisMigrationId = "20260923110412_AddChatWorkflowBinding";

    [Test]
    public async Task Migrate_AddsTheBindingColumnsTheForeignKeyAndBothIndexes_AndAnExistingRunReadsUnbound()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("chat-workflow-binding-up.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_runs")).Contains("conversation_id"), "the column must not exist before the migration.");
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_runs (id, request_id, definition_id, definition_version, graph_hash, status, failure_class, graph_json, seq, version, created_at_utc)
                                 VALUES ($id, $request, $definition, 1, 'hash', 'Completed', 'None', zeroblob(8), 0, 1, 0);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$definition", Guid.NewGuid().ToString().ToUpperInvariant());
            });

        await probe.MigrateToAsync(ThisMigrationId);

        var runColumns = await probe.ColumnsAsync("graph_workflow_runs");
        AssertEx.True(runColumns.Contains("conversation_id") && runColumns.Contains("trigger_message_id"));
        AssertEx.True((await probe.ColumnsAsync("graph_workflow_node_runs")).Contains("published_message_id"));
        AssertEx.True(await probe.ForeignKeyExistsAsync("graph_workflow_runs", "conversation_id", "conversations"));
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_runs", "ux_graph_workflow_runs_live_conversation", unique: true, "conversation_id"),
            "one live run per conversation is a database rule.");
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_runs", "ix_graph_workflow_runs_conversation_created", unique: false, "conversation_id", "created_at_utc"));
        AssertEx.Equal("SET NULL",
            await probe.ScalarAsync("SELECT on_delete FROM pragma_foreign_key_list('graph_workflow_runs') WHERE \"from\" = 'conversation_id';") as string);
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_runs WHERE conversation_id IS NULL;"),
            "a run from before chat workflows survives the table rebuild, unbound.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsTheBindingAndKeepsTheRuns()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("chat-workflow-binding-rollback.sqlite", ThisMigrationId);
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_runs (id, request_id, definition_id, definition_version, graph_hash, status, failure_class, graph_json, seq, version, created_at_utc)
                                 VALUES ($id, $request, $definition, 1, 'hash', 'Completed', 'None', zeroblob(8), 0, 1, 0);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$definition", Guid.NewGuid().ToString().ToUpperInvariant());
            });

        await probe.MigrateToAsync(PreviousMigrationId);

        AssertEx.False((await probe.ColumnsAsync("graph_workflow_runs")).Contains("conversation_id"), "Down must drop the binding Up added.");
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_node_runs")).Contains("published_message_id"));
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_runs;"), "and the rows survive the rebuild.");
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_runs", "ux_graph_workflow_runs_request_id", unique: true, "request_id"),
            "the sibling index survives too.");
    }
}
