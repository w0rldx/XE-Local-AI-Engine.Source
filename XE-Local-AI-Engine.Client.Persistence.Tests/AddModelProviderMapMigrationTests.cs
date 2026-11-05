namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class AddModelProviderMapMigrationTests : IDisposable
{
    private const string PreModelProviderMapMigrationId = "20260610165152_EncryptConversationTitle";
    private const string PreRevisionMigrationId = "20260813121930_AddKnowledgeCollectionsAndProvenance";
    private const string PreRemoveOllamaProviderMapRowsMigrationId = "20261101053539_AddPlainConversations";
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

        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreModelProviderMapMigrationId);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = await OpenConnectionAsync(databasePath);

        AssertEx.True(await TableExistsAsync(connection, "model_provider_map"),
            "Migration should create the model_provider_map table.");

        var columns = await GetColumnsAsync(connection);
        AssertEx.True(columns.SetEquals(new[]
        {
            "model_name",
            "provider_name",
            "revision",
            "updated_at_utc"
        }), "model_provider_map should expose the mapped columns.");

        AssertEx.True(await ModelNameUsesNoCaseCollationAsync(connection),
            "model_provider_map.model_name should use NOCASE collation.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsModelProviderMap()
    {
        var databasePath = GetDatabasePath("model-provider-map-rollback.sqlite");

        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreModelProviderMapMigrationId);
        }

        await using var connection = await OpenConnectionAsync(databasePath);

        AssertEx.False(await TableExistsAsync(connection, "model_provider_map"),
            "Rollback should drop the model_provider_map table.");
    }

    [Test]
    public async Task RevisionMigration_BackfillsExistingRowsAndRollsBackWithoutDataLoss()
    {
        var databasePath = GetDatabasePath("model-provider-map-revision.sqlite");

        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreRevisionMigrationId);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.ExecuteSqlRawAsync("INSERT INTO model_provider_map (model_name, provider_name, updated_at_utc) VALUES ('legacy-model', 'llamacpp', 7);");
            await context.Database.MigrateAsync();
        }

        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT revision FROM model_provider_map WHERE model_name = 'legacy-model';";
            AssertEx.Equal("legacy", await command.ExecuteScalarAsync() as string);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreRevisionMigrationId);
        }

        await using var rolledBack = await OpenConnectionAsync(databasePath);
        var columns = await GetColumnsAsync(rolledBack);
        AssertEx.False(columns.Contains("revision"), "Rollback should remove only the revision column.");
        await using var providerCommand = rolledBack.CreateCommand();
        providerCommand.CommandText = "SELECT provider_name FROM model_provider_map WHERE model_name = 'legacy-model';";
        AssertEx.Equal("llamacpp", await providerCommand.ExecuteScalarAsync() as string);
    }

    [Test]
    public async Task RemoveOllamaProviderMapRows_RemovesThatProviderInAnyCaseAndKeepsOtherRows()
    {
        var databasePath = GetDatabasePath("model-provider-map-remove-runtime.sqlite");

        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreRemoveOllamaProviderMapRowsMigrationId);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.ExecuteSqlRawAsync("""
                                                      INSERT INTO model_provider_map (model_name, provider_name, updated_at_utc, revision) VALUES
                                                          ('old-lower', 'ollama', 1, 'r1'),
                                                          ('old-upper', 'OLLAMA', 2, 'r2'),
                                                          ('old-mixed', 'Ollama', 3, 'r3'),
                                                          ('kept-gguf', 'llamacpp', 4, 'r4'),
                                                          ('kept-external', 'external', 5, 'r5'),
                                                          ('kept-lookalike', 'ollama2', 6, 'r6');
                                                      """);
            await context.Database.MigrateAsync();
        }

        await using (var context = CreateContext(databasePath))
        {
            var store = new ModelProviderMapStore(context, TimeProvider.System);

            // No row left means the caller's routing default applies, which sends the name to the GGUF provider.
            AssertEx.Null(await store.GetProviderForModelAsync("old-lower"));

            var rows = await store.ListAsync();
            AssertEx.Equal("kept-external=external,kept-gguf=llamacpp,kept-lookalike=ollama2",
                string.Join(',', rows.Select(static row => $"{row.ModelName}={row.ProviderName}")));
        }
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static async Task<IReadOnlySet<string>> GetColumnsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM model_provider_map LIMIT 0;";
        await using var reader = await command.ExecuteReaderAsync();
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> ModelNameUsesNoCaseCollationAsync(SqliteConnection connection)
    {
        // The sqlite_master CREATE TABLE records per-column COLLATE; confirm model_name carries COLLATE NOCASE. PRAGMA
        // takes no parameters and the table name is a fixed literal, so no caller-supplied SQL reaches it.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'model_provider_map';";
        var sql = await command.ExecuteScalarAsync() as string;
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
