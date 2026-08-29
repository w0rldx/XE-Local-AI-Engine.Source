namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddDevWorkflowFoundationMigrationTests
{
    private const string PreviousMigrationId = "20260826102207_AddBenchmarkTaskItems";
    private const string ThisMigrationId = "20260828102539_AddDevWorkflowFoundation";

    private static readonly string[] Tables =
    [
        "dev_workflow_work_items",
        "dev_workflow_definitions",
        "dev_workflow_runs",
        "dev_workflow_node_runs",
        "dev_workflow_run_events",
        "dev_workflow_decisions",
        "dev_workflow_artifacts",
        "dev_workflow_artifact_uses"
    ];

    [Test]
    public async Task Migrate_CreatesTheEightTablesWithTheirColumnsAndIndexes()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("dev-workflow-foundation.sqlite", PreviousMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.False(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must not exist before the migration.");
        }

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist after the migration.");
        }

        var runColumns = await probe.ColumnsAsync("dev_workflow_runs").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "work_item_id",
                     "definition_id",
                     "definition_version",
                     "definition_graph_hash",
                     "graph_json",
                     "graph_revision",
                     "status",
                     "last_sequence",
                     "failure_class",
                     "terminal_reason",
                     "started_at_utc",
                     "ended_at_utc",
                     "created_at_utc",
                     "updated_at_utc",
                     "version"
                 })
        {
            AssertEx.True(runColumns.Contains(column), $"dev_workflow_runs must carry '{column}'.");
        }

        // The full node-run column set ships now, inert columns included: five nullable fields cost nothing and save a
        // migration when the parallelism and policy slices land.
        var nodeRunColumns = await probe.ColumnsAsync("dev_workflow_node_runs").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "run_id",
                     "node_key",
                     "node_type",
                     "attempt",
                     "max_attempts",
                     "session_resumes",
                     "status",
                     "queue_reason",
                     "pending_decision_kind",
                     "sequence",
                     "work_session_id",
                     "agent_definition_id",
                     "development_project_id",
                     "development_task_id",
                     "input_json",
                     "output_json",
                     "policy_resolution_json",
                     "materialized_from_node_run_id",
                     "materialization_index",
                     "failure_class",
                     "terminal_reason",
                     "queued_at_utc",
                     "started_at_utc",
                     "ended_at_utc",
                     "created_at_utc"
                 })
        {
            AssertEx.True(nodeRunColumns.Contains(column), $"dev_workflow_node_runs must carry '{column}'.");
        }

        var artifactColumns = await probe.ColumnsAsync("dev_workflow_artifacts").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "lineage_id",
                     "producing_node_key",
                     "produced_by_node_run_id",
                     "version",
                     "is_stale",
                     "stale_since_sequence",
                     "stale_because_artifact_id",
                     "stale_reason",
                     "managed_reference"
                 })
        {
            AssertEx.True(artifactColumns.Contains(column), $"dev_workflow_artifacts must carry '{column}'.");
        }

        AssertEx.True(await probe.IndexExistsAsync("dev_workflow_node_runs", "ux_dev_workflow_node_runs_run_node", unique: true, "run_id", "node_key").ConfigureAwait(false),
            "One row per (run, node key) is the node run's identity, so it is a unique index.");
        AssertEx.True(await probe.IndexExistsAsync("dev_workflow_run_events", "ux_dev_workflow_run_events_run_sequence", unique: true, "run_id", "sequence")
                                 .ConfigureAwait(false),
            "The event watermark must be unique per run.");
        AssertEx.True(await probe.IndexExistsAsync("dev_workflow_run_events", "ux_dev_workflow_run_events_operation", unique: true, "run_id", "operation_id")
                                 .ConfigureAwait(false),
            "One event per operation id is what makes a replayed step idempotent.");
        AssertEx.True(await probe.IndexExistsAsync("dev_workflow_runs", "ux_dev_workflow_runs_live_per_work_item", unique: true, "work_item_id").ConfigureAwait(false),
            "One live run per work item is a database constraint, which cannot lose a race the way a read-modify-write can.");
        AssertEx.True(await probe.IndexExistsAsync("dev_workflow_artifacts", "ux_dev_workflow_artifacts_lineage_version", unique: true, "lineage_id", "version")
                                 .ConfigureAwait(false),
            "The lineage is the version key.");
        AssertEx.True(await probe
                            .IndexExistsAsync("dev_workflow_artifacts", "ix_dev_workflow_artifacts_run_node_name", unique: false, "run_id", "producing_node_key", "name")
                            .ConfigureAwait(false),
            "Lineage resolution is (run, producing node key, name), and it must be one indexed read.");
        AssertEx.True(await probe.IndexExistsAsync("dev_workflow_decisions", "ux_dev_workflow_decisions_node_run_attempt", unique: true, "node_run_id", "attempt")
                                 .ConfigureAwait(false),
            "One decision per node-run ATTEMPT, not per node run.");
        AssertEx.True(await probe
                            .IndexExistsAsync("dev_workflow_artifact_uses", "ux_dev_workflow_artifact_uses_node_artifact", unique: true, "node_run_id", "artifact_id")
                            .ConfigureAwait(false),
            "A consumed-by edge is captured idempotently.");
    }

    /// <summary>T-10: the migrated schema and the one EnsureCreated builds must agree, or a fresh box and an upgraded one diverge.</summary>
    [Test]
    public async Task MigratedSchema_MatchesWhatEnsureCreatedBuilds()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("dev-workflow-schema-parity.sqlite").ConfigureAwait(false);

        using var fixture = new DevWorkflowTestFixture();
        await using var created = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        foreach (var table in Tables)
        {
            var migrated = await probe.ColumnsAsync(table).ConfigureAwait(false);
            var ensured = await EnsureCreatedColumnsAsync(fixture, table).ConfigureAwait(false);
            AssertEx.Empty(migrated.Except(ensured, StringComparer.Ordinal), $"{table}: the migration created column(s) EnsureCreated does not.");
            AssertEx.Empty(ensured.Except(migrated, StringComparer.Ordinal), $"{table}: EnsureCreated created column(s) the migration does not.");
        }
    }

    [Test]
    public async Task Rollback_DropsTheEightTablesAndLeavesTheRestOfTheSchemaIntact()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("dev-workflow-foundation-rollback.sqlite", ThisMigrationId).ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.False(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must be gone after the rollback.");
        }

        // Nothing else may go with them: the migration only ever created tables, so `Down` has nothing else to touch.
        AssertEx.True(await probe.TableExistsAsync("agent_work_sessions").ConfigureAwait(false), "The rollback must not disturb the work-session tables.");
        AssertEx.True(await probe.TableExistsAsync("development_tasks").ConfigureAwait(false), "The rollback must not disturb the Development tables.");
    }

    private static async Task<IReadOnlySet<string>> EnsureCreatedColumnsAsync(DevWorkflowTestFixture fixture, string table)
    {
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(ordinal: 0));
        }

        return columns;
    }
}
