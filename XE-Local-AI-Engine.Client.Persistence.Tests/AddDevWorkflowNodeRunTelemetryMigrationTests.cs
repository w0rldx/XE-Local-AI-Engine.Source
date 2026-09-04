namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddDevWorkflowNodeRunTelemetry</c> migration: it adds the twelve cost-telemetry columns to
///     <c>dev_workflow_node_runs</c> on both an upgrade from the preceding migration and a fresh migrate-to-head, drops
///     them on rollback, adds no index, and leaves no model/snapshot drift.
/// </summary>
public sealed class AddDevWorkflowNodeRunTelemetryMigrationTests : IDisposable
{
    private const string PreTelemetryMigrationId = "20260903104044_AddIntegrationFoundation";

    /// <summary>The twelve columns of P-C1 §4.1, in the plan's own order.</summary>
    private static readonly string[] TelemetryColumns =
    [
        "input_tokens",
        "output_tokens",
        "reasoning_tokens",
        "estimated_input_tokens",
        "provider_calls",
        "tool_calls",
        "tool_schema_tokens",
        "tool_names_json",
        "agent_turn_ms",
        "served_model_name",
        "route_json",
        "work_session_steps"
    ];

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsTheTelemetryColumns()
    {
        var databasePath = GetDatabasePath("node-run-telemetry-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreTelemetryMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnNamesAsync(connection).ConfigureAwait(false);

        foreach (var column in TelemetryColumns)
        {
            AssertEx.True(columns.Contains(column), $"dev_workflow_node_runs should expose the {column} column after the migration.");
        }
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsTheTelemetryColumns()
    {
        var databasePath = GetDatabasePath("node-run-telemetry-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnNamesAsync(connection).ConfigureAwait(false);

        foreach (var column in TelemetryColumns)
        {
            AssertEx.True(columns.Contains(column), $"A fresh migrate-to-head should expose the {column} column.");
        }
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsTheTelemetryColumns()
    {
        var databasePath = GetDatabasePath("node-run-telemetry-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreTelemetryMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnNamesAsync(connection).ConfigureAwait(false);

        foreach (var column in TelemetryColumns)
        {
            AssertEx.False(columns.Contains(column), $"Rolling back one migration should drop the {column} column.");
        }

        // The rollback rebuilds the table from the preceding migration's model, so the columns that were already there
        // are the ones a sibling slice would lose if this migration ever stopped being the tail.
        AssertEx.True(columns.Contains("failure_class"), "Rolling back the telemetry migration must not disturb the columns that preceded it.");
        AssertEx.True(columns.Contains("terminal_reason"), "Rolling back the telemetry migration must not disturb the columns that preceded it.");
    }

    /// <summary>
    ///     No index is added: every read of these columns is by <c>run_id</c>, which the node-run identity index already
    ///     covers, or a whole-table rollup. An index here would be a write cost paid on every settle for nothing.
    /// </summary>
    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsNoTelemetryIndex()
    {
        var databasePath = GetDatabasePath("node-run-telemetry-indexes.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var indexes = await GetIndexNamesAsync(connection).ConfigureAwait(false);

        string[] expected =
        [
            "ux_dev_workflow_node_runs_run_node",
            "ix_dev_workflow_node_runs_run_sequence",
            "ix_dev_workflow_node_runs_status",
            "ux_dev_workflow_node_runs_work_session",
            "ix_dev_workflow_node_runs_materialized_from",
            "ix_dev_workflow_node_runs_development_task"
        ];

        AssertEx.Equal(string.Join(", ", expected.Order(StringComparer.Ordinal)),
            string.Join(", ", indexes.Order(StringComparer.Ordinal)),
            "The telemetry migration adds columns only — the node-run index set is unchanged.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("node-run-telemetry-drift.sqlite");
        await using var context = CreateContext(databasePath);

        AssertEx.False(context.Database.HasPendingModelChanges(),
            "The NodeChat model has drifted from the latest migration snapshot — regenerate the migration.");
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection)
    {
        // PRAGMA table_info exposes each column name; the table name is a fixed literal so this stays free of caller SQL.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(dev_workflow_node_runs);";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static async Task<HashSet<string>> GetIndexNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'dev_workflow_node_runs' AND name NOT LIKE 'sqlite_%';";

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            indexes.Add(reader.GetString(ordinal: 0));
        }

        return indexes;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
