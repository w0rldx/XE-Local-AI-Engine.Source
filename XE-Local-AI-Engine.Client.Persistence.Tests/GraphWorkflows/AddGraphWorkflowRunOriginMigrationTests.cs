namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddGraphWorkflowRunOrigin</c>: the run's plaintext <c>trigger_source</c> (default Manual = 1), <c>principal_id</c>
///     and <c>key_prefix</c>, and the definition's <c>trigger_sources</c> (default Manual | Chat = 3).
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddGraphWorkflowRunOriginMigrationTests
{
    private const string PreviousMigrationId = "20261015102049_AddUnattendedActionFlags";
    private const string ThisMigrationId = "20261015113203_AddGraphWorkflowRunOrigin";

    [Test]
    public async Task Migrate_AddsTheOriginColumns_AndExistingRowsReadAsManualAndManualOrChat()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("graph-run-origin-up.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_runs")).Contains("trigger_source"), "the run column must not exist before the migration.");
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_definitions")).Contains("trigger_sources"), "the definition column must not exist before the migration.");
        await SeedRowsAsync(probe);

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_runs WHERE trigger_source = 1 AND principal_id IS NULL AND key_prefix IS NULL;"),
            "a run from before the columns reads as started by the operator, with no principal.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions WHERE trigger_sources = 3;"),
            "a definition from before the column accepts what it always did: Manual and Chat.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsTheOriginColumnsAndKeepsTheRows()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("graph-run-origin-rollback.sqlite", ThisMigrationId);
        await SeedRowsAsync(probe);

        await probe.MigrateToAsync(PreviousMigrationId);

        var runColumns = await probe.ColumnsAsync("graph_workflow_runs");
        var definitionColumns = await probe.ColumnsAsync("graph_workflow_definitions");
        AssertEx.False(runColumns.Contains("trigger_source") || runColumns.Contains("principal_id") || runColumns.Contains("key_prefix"),
            "Down must drop every run column Up added.");
        AssertEx.False(definitionColumns.Contains("trigger_sources"), "Down must drop the definition column Up added.");
        AssertEx.True(runColumns.Contains("trigger_message_id") && definitionColumns.Contains("allows_actions"), "the earlier columns survive the rollback.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_runs;"), "the run row survives it.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions;"), "the definition row survives it.");
    }

    private static async Task SeedRowsAsync(MigrationSchemaProbe probe)
    {
        var definitionId = Guid.NewGuid().ToString().ToUpperInvariant();
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_definitions (id, name, graph_json, graph_hash, node_count, schema_version, version, created_at_utc, updated_at_utc)
                                 VALUES ($id, 'Lights', zeroblob(8), 'hash', 1, 1, 1, 0, 0);
                                 """,
            command => command.Parameters.AddWithValue("$id", definitionId));
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_runs (id, request_id, definition_id, definition_version, graph_hash, status, failure_class, graph_json, seq, version, created_at_utc)
                                 VALUES ($id, $request, $definition, 1, 'hash', 'Completed', 'None', zeroblob(8), 0, 1, 0);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$definition", definitionId);
            });
    }
}
