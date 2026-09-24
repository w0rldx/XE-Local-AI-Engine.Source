namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddChatWorkflowNodes</c>: the plaintext, defaulted <c>kind</c> column on <c>graph_workflow_definitions</c> and its index.</summary>
/// <remarks>The default is load-bearing: a definition saved before chat workflows must read as Standard, never as an empty string.</remarks>
[Category(TestCategories.Integration)]
public sealed class AddChatWorkflowNodesMigrationTests
{
    private const string PreviousMigrationId = "20260913005440_AddTranscriptionSessions";
    private const string ThisMigrationId = "20260923005438_AddChatWorkflowNodes";

    [Test]
    public async Task Migrate_AddsTheDefaultedKindColumnAndItsIndex_AndAnExistingDefinitionReadsStandard()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("chat-workflow-nodes-up.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_definitions")).Contains("kind"), "the column must not exist before the migration.");
        var definitionId = Guid.NewGuid();
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_definitions (id, name, description, graph_json, graph_hash, node_count, schema_version, version, created_at_utc, updated_at_utc)
                                 VALUES ($id, 'Before chat', NULL, zeroblob(8), 'hash', 2, 1, 1, 0, 0);
                                 """,
            command => command.Parameters.AddWithValue("$id", definitionId.ToString().ToUpperInvariant()));

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.True((await probe.ColumnsAsync("graph_workflow_definitions")).Contains("kind"));
        AssertEx.Equal("'Standard'", await probe.ColumnDefaultAsync("graph_workflow_definitions", "kind"));
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_definitions", "ix_graph_workflow_definitions_kind", unique: false, "kind"),
            "the picker filters on kind, so it is indexed.");
        AssertEx.Equal("Standard", await probe.ScalarAsync("SELECT kind FROM graph_workflow_definitions WHERE name = 'Before chat';") as string,
            "a definition saved before chat workflows is a Standard one.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsTheKindColumnAndKeepsTheDefinitions()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("chat-workflow-nodes-rollback.sqlite", ThisMigrationId);
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_definitions (id, name, description, graph_json, graph_hash, node_count, schema_version, kind, version, created_at_utc, updated_at_utc)
                                 VALUES ($id, 'A chat workflow', NULL, zeroblob(8), 'hash', 2, 1, 'Chat', 1, 0, 0);
                                 """,
            command => command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant()));

        await probe.MigrateToAsync(PreviousMigrationId);

        var columns = await probe.ColumnsAsync("graph_workflow_definitions");
        AssertEx.False(columns.Contains("kind"), "Down must drop the column Up added.");
        foreach (var column in new[]
                 {
                     "id",
                     "name",
                     "graph_json",
                     "graph_hash",
                     "node_count",
                     "schema_version",
                     "version"
                 })
        {
            AssertEx.True(columns.Contains(column), $"Down rebuilds the table from its previous model and must keep '{column}'.");
        }

        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions;"), "and the rows survive the rebuild.");
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_definitions", "ix_graph_workflow_definitions_name", unique: false, "name"),
            "the sibling index survives too.");
    }
}
