namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddAgentRunEnvelopeColumns</c> migration: it adds the durable run-envelope columns to
///     the existing <c>agent_execution_logs</c> table on both an upgrade from the preceding migration and a fresh
///     migrate-to-head, drops them on rollback, and leaves no model/snapshot drift. The two discriminator columns are
///     NOT NULL with a default of 0 so pre-existing (adaptive-memory) rows backfill to record kind 0.
/// </summary>
public sealed class AddAgentRunEnvelopeColumnsMigrationTests : IDisposable
{
    private const string PreRunEnvelopeMigrationId = "20260713204544_AddChatMaintenanceState";

    private static readonly string[] EnvelopeColumns =
    [
        "record_kind",
        "schema_version",
        "invocation_id",
        "request_id",
        "terminal_status",
        "trace_id",
        "content_chunk_count",
        "reasoning_chunk_count"
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
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsEnvelopeColumns()
    {
        var databasePath = GetDatabasePath("run-envelope-up.sqlite");

        // Bring the schema up to exactly the migration before this one, then apply the rest, so this migration's Up is
        // exercised as an in-place upgrade of an existing agent_execution_logs table, not just a fresh create.
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRunEnvelopeMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnInfoAsync(connection).ConfigureAwait(false);

        foreach (var column in EnvelopeColumns)
        {
            AssertEx.True(columns.ContainsKey(column), $"agent_execution_logs should expose the {column} column after the migration.");
        }

        AssertEx.True(columns["record_kind"].NotNull, "record_kind must be NOT NULL (discriminator, defaults to 0).");
        AssertEx.True(columns["schema_version"].NotNull, "schema_version must be NOT NULL (defaults to 0).");
        AssertEx.False(columns["invocation_id"].NotNull, "invocation_id must be nullable.");
        AssertEx.False(columns["terminal_status"].NotNull, "terminal_status must be nullable.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsEnvelopeColumns()
    {
        var databasePath = GetDatabasePath("run-envelope-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnInfoAsync(connection).ConfigureAwait(false);

        foreach (var column in EnvelopeColumns)
        {
            AssertEx.True(columns.ContainsKey(column), $"A fresh migrate-to-head should expose the {column} column.");
        }
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsEnvelopeColumns()
    {
        var databasePath = GetDatabasePath("run-envelope-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRunEnvelopeMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnInfoAsync(connection).ConfigureAwait(false);

        foreach (var column in EnvelopeColumns)
        {
            AssertEx.False(columns.ContainsKey(column), $"Rolling back one migration should drop the {column} column.");
        }
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        // In-process snapshot-drift guard: the runtime model and the latest migration's snapshot must agree, so a future
        // entity change without a regenerated migration fails here rather than only on a real DB.
        var databasePath = GetDatabasePath("run-envelope-drift.sqlite");
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

    private static async Task<IReadOnlyDictionary<string, (bool NotNull, bool IsPrimaryKey)>> GetColumnInfoAsync(SqliteConnection connection)
    {
        // PRAGMA table_info exposes each column's NOT NULL flag (notnull) and primary-key position (pk, 0 = not part of
        // the PK). The table name is a fixed literal, so this stays free of caller-supplied SQL.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_execution_logs);";

        var columns = new Dictionary<string, (bool NotNull, bool IsPrimaryKey)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            var notNull = reader.GetInt64(reader.GetOrdinal("notnull")) != 0L;
            var isPrimaryKey = reader.GetInt64(reader.GetOrdinal("pk")) != 0L;
            columns[name] = (notNull, isPrimaryKey);
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
