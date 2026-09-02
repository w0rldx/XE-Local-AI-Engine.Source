namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddDevWorkflowRuleSetsMigrationTests
{
    private const string PreviousMigrationId = "20260830160604_WidenDevelopmentTasksPerProject";
    private const string ThisMigrationId = "20260902081629_AddDevWorkflowRuleSets";
    private const string Table = "dev_workflow_rule_sets";

    [Test]
    public async Task Migrate_CreatesTheRuleSetTableWithItsColumnsAndEnabledIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("dev-workflow-rule-sets.sqlite", PreviousMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync(Table).ConfigureAwait(false), $"{Table} must not exist before the migration.");

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync(Table).ConfigureAwait(false), $"{Table} must exist after the migration.");

        var columns = await probe.ColumnsAsync(Table).ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "name",
                     "description",
                     "scope_json",
                     "enabled",
                     "body",
                     "content_sha256",
                     "version",
                     "created_at_utc",
                     "updated_at_utc"
                 })
        {
            AssertEx.True(columns.Contains(column), $"{Table} must carry '{column}'.");
        }

        AssertEx.True(await probe.IndexExistsAsync(Table, "ix_dev_workflow_rule_sets_enabled", unique: false, "enabled").ConfigureAwait(false),
            "The resolver reads every ENABLED rule set, so that is the one indexed column — and it is deliberately not unique.");
    }

    /// <summary>T-10: the migrated schema and the one EnsureCreated builds must agree, or a fresh box and an upgraded one diverge.</summary>
    [Test]
    public async Task MigratedSchema_MatchesWhatEnsureCreatedBuilds()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("dev-workflow-rule-set-parity.sqlite").ConfigureAwait(false);

        using var fixture = new DevWorkflowTestFixture();
        await using var created = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var migrated = await probe.ColumnsAsync(Table).ConfigureAwait(false);
        var ensured = await EnsureCreatedColumnsAsync(fixture).ConfigureAwait(false);
        AssertEx.Empty(migrated.Except(ensured, StringComparer.Ordinal), $"{Table}: the migration created column(s) EnsureCreated does not.");
        AssertEx.Empty(ensured.Except(migrated, StringComparer.Ordinal), $"{Table}: EnsureCreated created column(s) the migration does not.");
    }

    [Test]
    public async Task Rollback_DropsTheRuleSetTableAndLeavesTheRestOfTheWorkflowSchemaIntact()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("dev-workflow-rule-sets-rollback.sqlite", ThisMigrationId).ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync(Table).ConfigureAwait(false), $"{Table} must be gone after the rollback.");

        // The migration only ever created one table, so `Down` has nothing else to touch — least of all the audit.
        AssertEx.True(await probe.TableExistsAsync("dev_workflow_node_runs").ConfigureAwait(false), "The rollback must not disturb the node-run table.");
        AssertEx.True(await probe.TableExistsAsync("dev_workflow_definitions").ConfigureAwait(false), "The rollback must not disturb the definition table.");
    }

    private static async Task<IReadOnlySet<string>> EnsureCreatedColumnsAsync(DevWorkflowTestFixture fixture)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", Table);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(ordinal: 0));
        }

        return columns;
    }
}
