namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddModelClassificationsMigrationTests : IDisposable
{
    private const string PreModelClassificationsMigrationId = "20260601195214_AddSchedulerTables";
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_CreatesModelClassificationsWithNoCaseNameKey()
    {
        var databasePath = GetDatabasePath("model-classifications-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreModelClassificationsMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "model_classifications").ConfigureAwait(false),
            "Migration should create the model_classifications table.");

        var columns = await GetColumnsAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.SetEquals(new[]
        {
            "model_name", "digest", "detected_kind", "detected_capabilities_json", "override_kind", "detected_at_utc",
            "updated_at_utc"
        }), "model_classifications should expose the mapped columns.");

        AssertEx.True(await ModelNameUsesNoCaseCollationAsync(connection).ConfigureAwait(false),
            "model_classifications.model_name should use NOCASE collation.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsModelClassifications()
    {
        var databasePath = GetDatabasePath("model-classifications-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreModelClassificationsMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "model_classifications").ConfigureAwait(false),
            "Rollback should drop the model_classifications table.");
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
        command.CommandText = "SELECT * FROM model_classifications LIMIT 0;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> ModelNameUsesNoCaseCollationAsync(SqliteConnection connection)
    {
        // The CREATE TABLE statement in sqlite_master records the per-column COLLATE clause; confirm model_name carries
        // COLLATE NOCASE. PRAGMA takes no parameters and the table name is a fixed literal, so this stays free of
        // caller-supplied SQL.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'model_classifications';";
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
