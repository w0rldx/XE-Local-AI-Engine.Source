namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddModelReadinessTelemetry</c> migration: it adds the nullable <c>model_readiness_ms</c> column
///     to BOTH <c>agent_execution_logs</c> (the per-turn run envelope) and <c>dev_workflow_node_runs</c> (the node-run
///     rollup) on an upgrade and on a fresh migrate-to-head, drops both on rollback without disturbing the columns that
///     preceded them, and leaves no model/snapshot drift.
/// </summary>
public sealed class AddModelReadinessTelemetryMigrationTests : IDisposable
{
    private const string ReadinessColumn = "model_readiness_ms";

    private const string PreReadinessMigrationId = "20260904121650_AddAiTrendsWave";

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
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsTheReadinessColumnToBothTables()
    {
        var databasePath = GetDatabasePath("model-readiness-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreReadinessMigrationId).ConfigureAwait(false);
        }

        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            // The precondition the rest of this test rests on: neither table had the column before the migration ran,
            // so its presence below is this migration's doing and not the previous one's.
            AssertEx.False((await GetColumnNamesAsync(connection, envelopeTable: true).ConfigureAwait(false)).Contains(ReadinessColumn),
                "agent_execution_logs must not carry the readiness column before this migration.");
            AssertEx.False((await GetColumnNamesAsync(connection, envelopeTable: false).ConfigureAwait(false)).Contains(ReadinessColumn),
                "dev_workflow_node_runs must not carry the readiness column before this migration.");
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var upgraded = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.True((await GetColumnNamesAsync(upgraded, envelopeTable: true).ConfigureAwait(false)).Contains(ReadinessColumn),
            "agent_execution_logs should expose the readiness column after the migration.");
        AssertEx.True((await GetColumnNamesAsync(upgraded, envelopeTable: false).ConfigureAwait(false)).Contains(ReadinessColumn),
            "dev_workflow_node_runs should expose the readiness column after the migration.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsTheReadinessColumnToBothTables()
    {
        var databasePath = GetDatabasePath("model-readiness-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.True((await GetColumnNamesAsync(connection, envelopeTable: true).ConfigureAwait(false)).Contains(ReadinessColumn),
            "A fresh migrate-to-head should expose the readiness column on agent_execution_logs.");
        AssertEx.True((await GetColumnNamesAsync(connection, envelopeTable: false).ConfigureAwait(false)).Contains(ReadinessColumn),
            "A fresh migrate-to-head should expose the readiness column on dev_workflow_node_runs.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsTheReadinessColumnFromBothTables()
    {
        var databasePath = GetDatabasePath("model-readiness-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreReadinessMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var envelopeColumns = await GetColumnNamesAsync(connection, envelopeTable: true).ConfigureAwait(false);
        var nodeRunColumns = await GetColumnNamesAsync(connection, envelopeTable: false).ConfigureAwait(false);

        AssertEx.False(envelopeColumns.Contains(ReadinessColumn), "Rolling back one migration should drop the readiness column from agent_execution_logs.");
        AssertEx.False(nodeRunColumns.Contains(ReadinessColumn), "Rolling back one migration should drop the readiness column from dev_workflow_node_runs.");

        // SQLite rebuilds a table to drop a column, so a rollback rebuilds from the PRECEDING migration's model — which
        // is exactly how a sibling slice's columns get silently lost. Pin the neighbours this migration sits beside.
        AssertEx.True(envelopeColumns.Contains("dispatched_tier"), "Rolling back the readiness migration must not disturb the envelope columns that preceded it.");
        AssertEx.True(envelopeColumns.Contains("tool_schema_tokens"), "Rolling back the readiness migration must not disturb the envelope columns that preceded it.");
        AssertEx.True(nodeRunColumns.Contains("agent_turn_ms"), "Rolling back the readiness migration must not disturb the node-run columns that preceded it.");
        AssertEx.True(nodeRunColumns.Contains("work_session_steps"), "Rolling back the readiness migration must not disturb the node-run columns that preceded it.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("model-readiness-drift.sqlite");
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

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, bool envelopeTable)
    {
        // PRAGMA takes no parameters, so the two statements are compile-time constants chosen by a flag rather than
        // built from a table name — each CommandText stays a constant (CA2100), as in the production writer.
        await using var command = connection.CreateCommand();
        if (envelopeTable)
        {
            command.CommandText = "PRAGMA table_info(agent_execution_logs);";
        }
        else
        {
            command.CommandText = "PRAGMA table_info(dev_workflow_node_runs);";
        }

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
