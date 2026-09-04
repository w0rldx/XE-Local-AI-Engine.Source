namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddGraphWorkflows</c> is the only migration the whole Graph Workflow plan set ships: the run engine and the
///     pause/tool slice add none. That is why the column lists below name the columns this slice never reads — they
///     are the thing standing between a later slice and a second migration.
/// </summary>
public sealed class AddGraphWorkflowsMigrationTests
{
    private const string PreviousMigrationId = "20260903104044_AddIntegrationFoundation";
    private const string ThisMigrationId = "20260904084628_AddGraphWorkflows";

    private static readonly string[] Tables =
    [
        "graph_workflow_definitions",
        "graph_workflow_runs",
        "graph_workflow_node_runs",
        "graph_workflow_run_events"
    ];

    [Test]
    public async Task Migrate_CreatesTheFourTablesWithTheirColumnsAndIndexes()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("graph-workflows.sqlite", PreviousMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.False(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must not exist before the migration.");
        }

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist after the migration.");
        }

        var definitionColumns = await probe.ColumnsAsync("graph_workflow_definitions").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "name",
                     "description",
                     "graph_json",
                     "graph_hash",
                     "node_count",
                     "schema_version",
                     "version",
                     "created_at_utc",
                     "updated_at_utc"
                 })
        {
            AssertEx.True(definitionColumns.Contains(column), $"graph_workflow_definitions must carry '{column}'.");
        }

        // graph_hash and version are two of the four inert columns: nothing in this slice reads either, and the run
        // engine that will needs them to be here rather than in a migration of its own.
        var runColumns = await probe.ColumnsAsync("graph_workflow_runs").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "request_id",
                     "definition_id",
                     "definition_version",
                     "graph_hash",
                     "status",
                     "failure_class",
                     "graph_json",
                     "input_json",
                     "output_json",
                     "seq",
                     "version",
                     "cancel_requested_at_utc",
                     "started_at_utc",
                     "completed_at_utc",
                     "created_at_utc"
                 })
        {
            AssertEx.True(runColumns.Contains(column), $"graph_workflow_runs must carry '{column}'.");
        }

        // decision_operation_id and decided_by_subject are the other two inert columns — the decide endpoint's
        // idempotency key and its decider, both of which belong to a later slice that ships no migration.
        var nodeRunColumns = await probe.ColumnsAsync("graph_workflow_node_runs").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "run_id",
                     "node_key",
                     "kind",
                     "status",
                     "attempt",
                     "pending_decision_kind",
                     "decision_operation_id",
                     "decided_by_subject",
                     "failure_class",
                     "error",
                     "input_json",
                     "output_json",
                     "invocation_id",
                     "started_at_utc",
                     "completed_at_utc",
                     "updated_at_utc"
                 })
        {
            AssertEx.True(nodeRunColumns.Contains(column), $"graph_workflow_node_runs must carry '{column}'.");
        }

        var eventColumns = await probe.ColumnsAsync("graph_workflow_run_events").ConfigureAwait(false);
        foreach (var column in new[]
                 {
                     "id",
                     "run_id",
                     "seq",
                     "event_type",
                     "node_key",
                     "detail_json",
                     "created_at_utc"
                 })
        {
            AssertEx.True(eventColumns.Contains(column), $"graph_workflow_run_events must carry '{column}'.");
        }

        // What HasConversion<string>() buys, asserted rather than assumed: an int enum mapping would still create the
        // column and would still pass every column-name assertion above, while making the durable log unreadable.
        foreach (var table in new[] { "graph_workflow_runs", "graph_workflow_node_runs" })
        {
            AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, table, "failure_class").ConfigureAwait(false),
                $"{table}.failure_class must be TEXT — the failure class is persisted by name, not by ordinal.");
        }

        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_node_runs", "ux_graph_workflow_node_runs_run_node", unique: true, "run_id", "node_key")
                                 .ConfigureAwait(false),
            "One row per (run, node key) is the node run's identity, so it is a unique index.");
        AssertEx.True(await probe
                            .IndexExistsAsync("graph_workflow_node_runs", "ux_graph_workflow_node_runs_decision_operation", unique: true, "run_id", "decision_operation_id")
                            .ConfigureAwait(false),
            "The decide endpoint's idempotency key is unique run-wide, filtered to the rows that carry one.");
        AssertEx.Equal("\"decision_operation_id\" IS NOT NULL",
            await IndexFilterAsync(probe, "ux_graph_workflow_node_runs_decision_operation").ConfigureAwait(false),
            "Unfiltered, the index would let exactly one node run per run stay undecided.");
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_run_events", "ux_graph_workflow_run_events_run_seq", unique: true, "run_id", "seq").ConfigureAwait(false),
            "The event watermark must be unique per run.");
        AssertEx.True(await probe.IndexExistsAsync("graph_workflow_runs", "ux_graph_workflow_runs_request_id", unique: true, "request_id").ConfigureAwait(false),
            "The caller-minted request id is what makes a retried start idempotent, so it is a database constraint.");
    }

    /// <summary>The migrated schema and the one EnsureCreated builds must agree, or a fresh box and an upgraded one diverge.</summary>
    [Test]
    public async Task MigratedSchema_MatchesWhatEnsureCreatedBuilds()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("graph-workflows-schema-parity.sqlite").ConfigureAwait(false);

        using var fixture = new GraphWorkflowTestFixture();
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
    public async Task Rollback_DropsTheFourTablesAndLeavesTheRestOfTheSchemaIntact()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("graph-workflows-rollback.sqlite", ThisMigrationId).ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.False(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must be gone after the rollback.");
        }

        // Nothing else may go with them: the migration only ever created tables, so `Down` has nothing else to touch.
        AssertEx.True(await probe.TableExistsAsync("dev_workflow_runs").ConfigureAwait(false), "The rollback must not disturb the Dev Workflow tables.");
        AssertEx.True(await probe.TableExistsAsync("agent_work_sessions").ConfigureAwait(false), "The rollback must not disturb the work-session tables.");
    }

    private static async Task<string?> ColumnTypeAsync(MigrationSchemaProbe probe, string table, string column)
    {
        var value = await probe.ScalarAsync("SELECT type FROM pragma_table_info($table) WHERE name = $column;",
                            command =>
                            {
                                command.Parameters.AddWithValue("$table", table);
                                command.Parameters.AddWithValue("$column", column);
                            })
                        .ConfigureAwait(false);
        return value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> IndexFilterAsync(MigrationSchemaProbe probe, string indexName)
    {
        var value = await probe.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name;",
                            command => command.Parameters.AddWithValue("$name", indexName))
                        .ConfigureAwait(false);
        var sql = value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        var whereIndex = sql?.IndexOf(" WHERE ", StringComparison.Ordinal) ?? -1;
        return whereIndex < 0 ? null : sql![(whereIndex + " WHERE ".Length)..].Trim();
    }

    private static async Task<IReadOnlySet<string>> EnsureCreatedColumnsAsync(GraphWorkflowTestFixture fixture, string table)
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
