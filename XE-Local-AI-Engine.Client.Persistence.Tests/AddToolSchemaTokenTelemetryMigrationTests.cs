namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddToolSchemaTokenTelemetry</c> migration: it adds the two per-turn tool-schema token columns to
///     <c>agent_execution_logs</c> and the per-agent tool-relevance opt-out to <c>agent_definitions</c>, on both an
///     upgrade from the preceding migration and a fresh migrate-to-head; a row written before the migration reads back
///     null on the new columns; rollback drops all three; and the model has no snapshot drift.
/// </summary>
public sealed class AddToolSchemaTokenTelemetryMigrationTests : IDisposable
{
    private const string PreTelemetryMigrationId = "20260903104044_AddIntegrationFoundation";

    private static readonly string[] TelemetryColumns = ["tool_schema_tokens", "max_tool_schema_tokens"];

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
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsTheTokenColumnsAndTheOptOut()
    {
        var databasePath = GetDatabasePath("tool-schema-tokens-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreTelemetryMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var logColumns = await GetColumnNamesAsync(connection, "agent_execution_logs").ConfigureAwait(false);
        foreach (var column in TelemetryColumns)
        {
            AssertEx.True(logColumns.Contains(column), $"agent_execution_logs should expose the {column} column after the migration.");
        }

        var definitionColumns = await GetColumnNamesAsync(connection, "agent_definitions").ConfigureAwait(false);
        AssertEx.True(definitionColumns.Contains("disable_tool_relevance_filter"),
            "agent_definitions should expose the per-agent tool-relevance opt-out after the migration.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsTheTokenColumnsAndTheOptOut()
    {
        var databasePath = GetDatabasePath("tool-schema-tokens-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var logColumns = await GetColumnNamesAsync(connection, "agent_execution_logs").ConfigureAwait(false);
        foreach (var column in TelemetryColumns)
        {
            AssertEx.True(logColumns.Contains(column), $"A fresh migrate-to-head should expose the {column} column.");
        }

        var definitionColumns = await GetColumnNamesAsync(connection, "agent_definitions").ConfigureAwait(false);
        AssertEx.True(definitionColumns.Contains("disable_tool_relevance_filter"),
            "A fresh migrate-to-head should expose the per-agent tool-relevance opt-out.");
    }

    [Test]
    public async Task MigrateAsync_OverAPreMigrationRow_ReadsTheNewColumnsBackAsNull()
    {
        // The columns are added with no backfill, so an envelope row written before the migration must survive it and
        // report null rather than a manufactured zero — which is what lets a reader tell "not measured" from "measured
        // as none".
        var databasePath = GetDatabasePath("tool-schema-tokens-existing-row.sqlite");
        var rowId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreTelemetryMigrationId).ConfigureAwait(false);
        }

        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                                 INSERT INTO agent_execution_logs (id, record_kind, schema_version, agent_definition_id, model_name, config_hash, latency_ms, success, created_at_utc)
                                 VALUES ($id, 1, 3, $agent, 'model', '', 10, 1, 1);
                                 """;
            insert.Parameters.AddWithValue("$id", rowId.ToString());
            insert.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
            _ = await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var readConnection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var select = readConnection.CreateCommand();
        select.CommandText = "SELECT tool_schema_tokens, max_tool_schema_tokens FROM agent_execution_logs WHERE id = $id;";
        select.Parameters.AddWithValue("$id", rowId.ToString());
        await using var reader = await select.ExecuteReaderAsync().ConfigureAwait(false);

        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false), "The pre-migration row must survive the migration.");
        AssertEx.True(await reader.IsDBNullAsync(0).ConfigureAwait(false), "An existing row reads back null for the cumulative estimate.");
        AssertEx.True(await reader.IsDBNullAsync(1).ConfigureAwait(false), "An existing row reads back null for the per-round maximum.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsTheTokenColumnsAndTheOptOut()
    {
        var databasePath = GetDatabasePath("tool-schema-tokens-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreTelemetryMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var logColumns = await GetColumnNamesAsync(connection, "agent_execution_logs").ConfigureAwait(false);
        foreach (var column in TelemetryColumns)
        {
            AssertEx.False(logColumns.Contains(column), $"Rolling back one migration should drop the {column} column.");
        }

        var definitionColumns = await GetColumnNamesAsync(connection, "agent_definitions").ConfigureAwait(false);
        AssertEx.False(definitionColumns.Contains("disable_tool_relevance_filter"),
            "Rolling back one migration should drop the per-agent tool-relevance opt-out.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("tool-schema-tokens-drift.sqlite");
        await using var context = CreateContext(databasePath);

        AssertEx.False(context.Database.HasPendingModelChanges(),
            "The NodeChat model has drifted from the latest migration snapshot — regenerate the migration.");
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        // PRAGMA table_info takes the table as a bound argument, so no caller string reaches the statement text.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
