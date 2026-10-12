namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddGraphWorkflowRunPrevious</c>: the nullable, encrypted <c>previous_json</c> column on runs.</summary>
[Category(TestCategories.Integration)]
public sealed class AddGraphWorkflowRunPreviousMigrationTests
{
    private const string PreviousMigrationId = "20261012173207_AddQuartzPreferredNodeColumns";
    private const string ThisMigrationId = "20261012212445_AddGraphWorkflowRunPrevious";

    [Test]
    public async Task Migrate_AddsThePreviousColumn_AndAnExistingRunReadsWithoutOne()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("graph-workflow-run-previous-up.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_runs")).Contains("previous_json"), "the column must not exist before the migration.");
        await SeedRunAsync(probe);

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.True((await probe.ColumnsAsync("graph_workflow_runs")).Contains("previous_json"));
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_runs WHERE previous_json IS NULL;"), "a run from before the column has no previous run pinned.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsTheColumnAndKeepsTheRuns()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("graph-workflow-run-previous-rollback.sqlite", ThisMigrationId);
        await SeedRunAsync(probe);

        await probe.MigrateToAsync(PreviousMigrationId);

        var columns = await probe.ColumnsAsync("graph_workflow_runs");
        AssertEx.False(columns.Contains("previous_json"), "Down must drop the column Up added.");
        AssertEx.True(columns.Contains("output_json") && columns.Contains("conversation_id"), "the earlier columns survive the rollback.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_runs;"), "and the rows survive it.");
    }

    private static async Task SeedRunAsync(MigrationSchemaProbe probe) =>
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
}
