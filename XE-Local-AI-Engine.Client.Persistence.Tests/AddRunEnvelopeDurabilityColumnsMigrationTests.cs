namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddRunEnvelopeDurabilityColumns</c> migration: it adds the v2 lifecycle columns
///     (reasoning/total tokens + started_at_utc) and the filtered UNIQUE index that makes the run envelope idempotent on
///     the assistant message id, on both an upgrade from the preceding migration and a fresh migrate-to-head; drops them
///     on rollback; and leaves no model/snapshot drift.
/// </summary>
public sealed class AddRunEnvelopeDurabilityColumnsMigrationTests : IDisposable
{
    private const string PreDurabilityMigrationId = "20260714144229_AddAgentRunEnvelopeColumns";
    private const string EnvelopeUniqueIndexName = "ix_agent_execution_logs_envelope_message_id";

    private static readonly string[] DurabilityColumns = ["reasoning_tokens", "total_tokens", "started_at_utc"];

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
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsColumnsAndUniqueIndex()
    {
        var databasePath = GetDatabasePath("run-envelope-durability-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDurabilityMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnNamesAsync(connection).ConfigureAwait(false);

        foreach (var column in DurabilityColumns)
        {
            AssertEx.True(columns.Contains(column), $"agent_execution_logs should expose the {column} column after the migration.");
        }

        AssertEx.True(await IndexExistsAsync(connection, EnvelopeUniqueIndexName).ConfigureAwait(false),
            "The migration should create the filtered unique envelope index.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsColumnsAndUniqueIndex()
    {
        var databasePath = GetDatabasePath("run-envelope-durability-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnNamesAsync(connection).ConfigureAwait(false);

        foreach (var column in DurabilityColumns)
        {
            AssertEx.True(columns.Contains(column), $"A fresh migrate-to-head should expose the {column} column.");
        }

        AssertEx.True(await IndexExistsAsync(connection, EnvelopeUniqueIndexName).ConfigureAwait(false),
            "A fresh migrate-to-head should create the filtered unique envelope index.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsColumnsAndUniqueIndex()
    {
        var databasePath = GetDatabasePath("run-envelope-durability-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDurabilityMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnNamesAsync(connection).ConfigureAwait(false);

        foreach (var column in DurabilityColumns)
        {
            AssertEx.False(columns.Contains(column), $"Rolling back one migration should drop the {column} column.");
        }

        AssertEx.False(await IndexExistsAsync(connection, EnvelopeUniqueIndexName).ConfigureAwait(false),
            "Rolling back one migration should drop the filtered unique envelope index.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("run-envelope-durability-drift.sqlite");
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

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name = $name;";
        command.Parameters.AddWithValue("$name", indexName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection)
    {
        // PRAGMA table_info exposes each column name; the table name is a fixed literal so this stays free of caller SQL.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_execution_logs);";

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
