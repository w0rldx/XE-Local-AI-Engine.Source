namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddGraphWorkflowSteering</c>: the nullable, encrypted <c>steering_json</c> column on node runs.</summary>
[Category(TestCategories.Integration)]
public sealed class AddGraphWorkflowSteeringMigrationTests
{
    private const string PreviousMigrationId = "20260923110412_AddChatWorkflowBinding";
    private const string ThisMigrationId = "20260923202445_AddGraphWorkflowSteering";

    [Test]
    public async Task Migrate_AddsTheSteeringColumn_AndAnExistingNodeRunReadsUnsteered()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("graph-workflow-steering-up.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_node_runs")).Contains("steering_json"), "the column must not exist before the migration.");
        await SeedNodeRunAsync(probe);

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.True((await probe.ColumnsAsync("graph_workflow_node_runs")).Contains("steering_json"));
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_node_runs WHERE steering_json IS NULL;"), "a node run from before steering reads unsteered.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsTheColumnAndKeepsTheNodeRuns()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("graph-workflow-steering-rollback.sqlite", ThisMigrationId);
        await SeedNodeRunAsync(probe);

        await probe.MigrateToAsync(PreviousMigrationId);

        var columns = await probe.ColumnsAsync("graph_workflow_node_runs");
        AssertEx.False(columns.Contains("steering_json"), "Down must drop the column Up added.");
        AssertEx.True(columns.Contains("published_message_id"), "the previous migration's column survives the rollback.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_node_runs;"), "and the rows survive it.");
    }

    private static async Task SeedNodeRunAsync(MigrationSchemaProbe probe)
    {
        var runId = Guid.NewGuid().ToString().ToUpperInvariant();
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_runs (id, request_id, definition_id, definition_version, graph_hash, status, failure_class, graph_json, seq, version, created_at_utc)
                                 VALUES ($id, $request, $definition, 1, 'hash', 'Running', 'None', zeroblob(8), 0, 1, 0);
                                 INSERT INTO graph_workflow_node_runs (id, run_id, node_key, kind, status, attempt, failure_class, updated_at_utc)
                                 VALUES ($nodeRun, $id, 'analyze', 'Agent', 'Running', 1, 'None', 0);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", runId);
                command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$definition", Guid.NewGuid().ToString().ToUpperInvariant());
                command.Parameters.AddWithValue("$nodeRun", Guid.NewGuid().ToString().ToUpperInvariant());
            });
    }
}
