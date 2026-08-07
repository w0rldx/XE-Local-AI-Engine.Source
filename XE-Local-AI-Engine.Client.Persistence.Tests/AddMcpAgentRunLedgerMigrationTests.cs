namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddMcpAgentRunLedgerMigrationTests : IDisposable
{
    private const string PreLedgerMigrationId = "20260804220941_AddAgentSkillImportProvenance";
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
    public async Task MigrateAsync_WhenApplied_CreatesRunAndSingletonLedgerTables()
    {
        var databasePath = GetDatabasePath("mcp-ledger-up.sqlite");
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreLedgerMigrationId).ConfigureAwait(false);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.True(await TableExistsAsync(connection, "mcp_agent_runs").ConfigureAwait(false));
        AssertEx.True(await TableExistsAsync(connection, "mcp_agent_run_ledger").ConfigureAwait(false));
        var ledgerColumns = await GetColumnsAsync(connection, "mcp_agent_run_ledger").ConfigureAwait(false);
        AssertEx.True(ledgerColumns.IsSupersetOf(new[] { "queued_run_count", "running_run_count", "nonterminal_run_count" }));
        var columns = await GetColumnsAsync(connection, "mcp_agent_runs").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "request_id", "request_fingerprint", "accounting_version", "status", "version", "claim_token",
            "stop_reason", "stop_requested_at_utc", "model_id", "model_override_id", "binding_fingerprint",
            "task_payload", "instructions_payload", "result_payload", "display_payload", "active_payload_bytes",
            "tombstone_logical_bytes", "payload_expires_at_utc", "compacted_at_utc"
        }), "The run table must contain the durable lifecycle, binding, encrypted payload, and accounting columns.");
        AssertEx.Equal(expected: 0L, await ForeignKeyCountAsync(connection).ConfigureAwait(false));

        await using var singleton = connection.CreateCommand();
        singleton.CommandText = "SELECT accounting_version, identity_count, active_payload_bytes FROM mcp_agent_run_ledger WHERE id = 1;";
        await using var reader = await singleton.ExecuteReaderAsync().ConfigureAwait(false);
        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false), "The migration must seed the singleton ledger row.");
        AssertEx.Equal(expected: 1L, reader.GetInt64(0));
        AssertEx.Equal(expected: 0L, reader.GetInt64(1));
        AssertEx.Equal(expected: 0L, reader.GetInt64(2));
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsBothLedgerTables()
    {
        var databasePath = GetDatabasePath("mcp-ledger-down.sqlite");
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreLedgerMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.False(await TableExistsAsync(connection, "mcp_agent_runs").ConfigureAwait(false));
        AssertEx.False(await TableExistsAsync(connection, "mcp_agent_run_ledger").ConfigureAwait(false));
    }

    private NodeChatDbContext CreateContext(string databasePath) => AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", tableName);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            _ = columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<long> ForeignKeyCountAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_foreign_key_list('mcp_agent_runs');";
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
