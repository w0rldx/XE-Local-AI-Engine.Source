namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddMcpServersMigrationTests : IDisposable
{
    private const string PreMcpServersMigrationId = "20260530050246_AddAgentDefinitions";
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
    public async Task MigrateAsync_WhenApplied_CreatesMcpServersWithUniqueNameIndex()
    {
        var databasePath = GetDatabasePath("mcp-servers-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreMcpServersMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "mcp_servers").ConfigureAwait(false),
            "Migration should create the mcp_servers table.");

        var columns = await GetMcpServerColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "name",
            "description",
            "transport_kind",
            "command",
            "arguments",
            "working_directory",
            "env",
            "url",
            "enabled",
            "version",
            "created_at_utc",
            "updated_at_utc"
        }), "mcp_servers should expose the mapped columns.");

        AssertEx.True(await UniqueIndexOnNameExistsAsync(connection).ConfigureAwait(false),
            "Migration should create a unique index on mcp_servers.name.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsMcpServers()
    {
        var databasePath = GetDatabasePath("mcp-servers-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreMcpServersMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "mcp_servers").ConfigureAwait(false),
            "Rollback should drop the mcp_servers table.");
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

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<bool> UniqueIndexOnNameExistsAsync(SqliteConnection connection)
    {
        // EF emits the unique index over name as IX_mcp_servers_name on a single column; confirm it is present, marked
        // unique by index_list, and covers exactly the name column. PRAGMA index_list takes no parameters, so this
        // stays free of string interpolation.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list('mcp_servers');";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        var nameOrdinal = reader.GetOrdinal("name");
        var uniqueOrdinal = reader.GetOrdinal("unique");

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var indexName = reader.GetString(nameOrdinal);
            var isUnique = reader.GetBoolean(uniqueOrdinal);
            if (isUnique && string.Equals(indexName, "IX_mcp_servers_name", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IReadOnlySet<string>> GetMcpServerColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM mcp_servers LIMIT 0;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
