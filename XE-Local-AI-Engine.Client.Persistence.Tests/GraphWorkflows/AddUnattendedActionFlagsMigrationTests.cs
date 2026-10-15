namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddUnattendedActionFlags</c>: the plaintext <c>allow_unattended</c> column on custom tools and
///     <c>allows_actions</c> on graph workflow definitions, both defaulting to false.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddUnattendedActionFlagsMigrationTests
{
    private const string PreviousMigrationId = "20261012212445_AddGraphWorkflowRunPrevious";
    private const string ThisMigrationId = "20261015102049_AddUnattendedActionFlags";

    [Test]
    public async Task Migrate_AddsBothFlags_AndExistingRowsReadAsFalse()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("unattended-action-flags-up.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("custom_tools")).Contains("allow_unattended"), "the tool column must not exist before the migration.");
        AssertEx.False((await probe.ColumnsAsync("graph_workflow_definitions")).Contains("allows_actions"), "the definition column must not exist before the migration.");
        await SeedRowsAsync(probe);

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM custom_tools WHERE allow_unattended = 0;"), "a tool from before the column is not allowed unattended.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions WHERE allows_actions = 0;"), "a definition from before the column allows no actions.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsBothColumnsAndKeepsTheRows()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("unattended-action-flags-rollback.sqlite", ThisMigrationId);
        await SeedRowsAsync(probe);

        await probe.MigrateToAsync(PreviousMigrationId);

        var toolColumns = await probe.ColumnsAsync("custom_tools");
        var definitionColumns = await probe.ColumnsAsync("graph_workflow_definitions");
        AssertEx.False(toolColumns.Contains("allow_unattended"), "Down must drop the tool column Up added.");
        AssertEx.False(definitionColumns.Contains("allows_actions"), "Down must drop the definition column Up added.");
        AssertEx.True(toolColumns.Contains("acknowledged") && definitionColumns.Contains("kind"), "the earlier columns survive the rollback.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM custom_tools;"), "the tool row survives it.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions;"), "the definition row survives it.");
    }

    private static async Task SeedRowsAsync(MigrationSchemaProbe probe)
    {
        await probe.ExecuteAsync("""
                                 INSERT INTO custom_tools (id, name, description, kind, mode, config_json, version, created_at_utc, updated_at_utc)
                                 VALUES ($id, 'custom__lights', zeroblob(8), 0, 0, zeroblob(8), 1, 0, 0);
                                 """,
            command => command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant()));
        await probe.ExecuteAsync("""
                                 INSERT INTO graph_workflow_definitions (id, name, graph_json, graph_hash, node_count, schema_version, version, created_at_utc, updated_at_utc)
                                 VALUES ($id, 'Lights', zeroblob(8), 'hash', 1, 1, 1, 0, 0);
                                 """,
            command => command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant()));
    }
}
