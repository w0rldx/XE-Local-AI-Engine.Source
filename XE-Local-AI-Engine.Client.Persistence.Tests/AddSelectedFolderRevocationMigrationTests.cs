namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddSelectedFolderRevocationMigrationTests : IDisposable
{
    private const string PreRevocationMigrationId = "20260806181000_AddMcpAgentRunLedger";
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
    public async Task MigrateAsync_AddsNullableRevocationAndActiveOnlyAliasIndex()
    {
        var databasePath = GetDatabasePath("selected-folder-revocation.sqlite");
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRevocationMigrationId).ConfigureAwait(false);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        AssertEx.True(await ColumnExistsAsync(connection, "revoked_at_utc").ConfigureAwait(false),
            "The selected-folder table must retain revoked rows with a nullable timestamp.");

        var sql = await GetAliasIndexSqlAsync(connection).ConfigureAwait(false);
        AssertEx.True(sql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));
        AssertEx.True(sql.Contains("revoked_at_utc IS NULL", StringComparison.OrdinalIgnoreCase),
            "Only active aliases should participate in the uniqueness constraint.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DiscardsRevokedRowsAndRestoresGlobalAliasUniqueness()
    {
        var databasePath = GetDatabasePath("selected-folder-revocation-rollback.sqlite");
        var activeId = Guid.NewGuid();
        var revokedId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertSelectedFolderAsync(connection, activeId, "shared-alias", revokedAtUtc: null).ConfigureAwait(false);
            await InsertSelectedFolderAsync(connection, revokedId, "shared-alias", revokedAtUtc: 1_000).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRevocationMigrationId).ConfigureAwait(false);
        }

        await using var downgradedConnection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.False(await ColumnExistsAsync(downgradedConnection, "revoked_at_utc").ConfigureAwait(false),
            "The pre-revocation schema must not expose the revocation timestamp.");
        AssertEx.True(await SelectedFolderExistsAsync(downgradedConnection, activeId).ConfigureAwait(false),
            "An active selected folder must survive the representational downgrade.");
        AssertEx.False(await SelectedFolderExistsAsync(downgradedConnection, revokedId).ConfigureAwait(false),
            "A revoked selected folder cannot be represented by the old schema and must be discarded.");

        var indexSql = await GetAliasIndexSqlAsync(downgradedConnection).ConfigureAwait(false);
        AssertEx.True(indexSql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(indexSql.Contains("WHERE", StringComparison.OrdinalIgnoreCase),
            "The downgraded alias index must enforce uniqueness across every remaining row.");

        var exception = await AssertEx.ThrowsAsync<SqliteException>(() =>
            InsertLegacySelectedFolderAsync(downgradedConnection, Guid.NewGuid(), "shared-alias")).ConfigureAwait(false);
        AssertEx.Equal(19, exception.SqliteErrorCode,
            "Inserting a duplicate alias after rollback must fail with a SQLite constraint violation.");
    }

    private NodeChatDbContext CreateContext(string databasePath) =>
        AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task InsertSelectedFolderAsync(SqliteConnection connection, Guid id, string alias, long? revokedAtUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO selected_folders (id, alias, host_path, mode, created_at_utc, revoked_at_utc)
                              VALUES ($id, $alias, $hostPath, 0, 100, $revokedAtUtc);
                              """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$hostPath", new byte[]
        {
            1,
            2,
            3
        });
        command.Parameters.AddWithValue("$revokedAtUtc", revokedAtUtc is null ? DBNull.Value : revokedAtUtc.Value);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task InsertLegacySelectedFolderAsync(SqliteConnection connection, Guid id, string alias)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO selected_folders (id, alias, host_path, mode, created_at_utc)
                              VALUES ($id, $alias, $hostPath, 0, 100);
                              """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$alias", alias);
        command.Parameters.AddWithValue("$hostPath", new byte[]
        {
            4,
            5,
            6
        });
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> SelectedFolderExistsAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM selected_folders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<string> GetAliasIndexSqlAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'IX_selected_folders_alias';";
        return AssertEx.NotNull(await command.ExecuteScalarAsync().ConfigureAwait(false) as string);
    }

    private static async Task<bool> ColumnExistsAsync(SqliteConnection connection, string columnName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info('selected_folders') WHERE name = $name;";
        command.Parameters.AddWithValue("$name", columnName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
