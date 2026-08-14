namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddModelProviderMapMigrationTests : IDisposable
{
    private const string PreModelProviderMapMigrationId = "20260610165152_EncryptConversationTitle";
    private const string PreRevisionMigrationId = "20260813121930_AddKnowledgeCollectionsAndProvenance";
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
    public async Task MigrateAsync_WhenApplied_CreatesModelProviderMapWithNoCaseNameKey()
    {
        var databasePath = GetDatabasePath("model-provider-map-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreModelProviderMapMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "model_provider_map").ConfigureAwait(false),
            "Migration should create the model_provider_map table.");

        var columns = await GetColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.SetEquals(new[]
        {
            "model_name",
            "provider_name",
            "revision",
            "updated_at_utc"
        }), "model_provider_map should expose the mapped columns.");

        AssertEx.True(await ModelNameUsesNoCaseCollationAsync(connection).ConfigureAwait(false),
            "model_provider_map.model_name should use NOCASE collation.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsModelProviderMap()
    {
        var databasePath = GetDatabasePath("model-provider-map-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreModelProviderMapMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "model_provider_map").ConfigureAwait(false),
            "Rollback should drop the model_provider_map table.");
    }

    [Test]
    public async Task RevisionMigration_BackfillsExistingRowsAndRollsBackWithoutDataLoss()
    {
        var databasePath = GetDatabasePath("model-provider-map-revision.sqlite");
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRevisionMigrationId).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO model_provider_map (model_name, provider_name, updated_at_utc) VALUES ('legacy-model', 'ollama', 7);")
                         .ConfigureAwait(false);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT revision FROM model_provider_map WHERE model_name = 'legacy-model';";
            AssertEx.Equal("legacy", await command.ExecuteScalarAsync().ConfigureAwait(false) as string);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRevisionMigrationId).ConfigureAwait(false);
        }

        await using var rolledBack = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetColumnsAsync(rolledBack).ConfigureAwait(false);
        AssertEx.False(columns.Contains("revision"), "Rollback should remove only the revision column.");
        await using var providerCommand = rolledBack.CreateCommand();
        providerCommand.CommandText = "SELECT provider_name FROM model_provider_map WHERE model_name = 'legacy-model';";
        AssertEx.Equal("ollama", await providerCommand.ExecuteScalarAsync().ConfigureAwait(false) as string);
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

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM model_provider_map LIMIT 0;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> ModelNameUsesNoCaseCollationAsync(SqliteConnection connection)
    {
        // The CREATE TABLE statement in sqlite_master records the per-column COLLATE clause; confirm model_name carries
        // COLLATE NOCASE. PRAGMA takes no parameters and the table name is a fixed literal, so this stays free of
        // caller-supplied SQL.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'model_provider_map';";
        var sql = await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
        return sql is not null
               && sql.Contains("model_name", StringComparison.Ordinal)
               && sql.Contains("NOCASE", StringComparison.OrdinalIgnoreCase);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
