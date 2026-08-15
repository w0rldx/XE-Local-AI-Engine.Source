namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddTrainingRunsMigrationTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddTrainingRunsMigration_RoundTrips()
    {
        _ = Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "migration.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        foreach (var table in new[]
                 {
                     "training_runs",
                     "training_work_items",
                     "training_artifacts"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            AssertEx.Equal(expected: 1L, (long)(await command.ExecuteScalarAsync())!, $"Migration should create {table}.");
        }

        await using (var queueCommand = connection.CreateCommand())
        {
            queueCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'training_work_items';";
            var sql = (string)(await queueCommand.ExecuteScalarAsync())!;
            AssertEx.True(sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase), "Queue sequence must be durable SQLite AUTOINCREMENT.");
            AssertEx.True(sql.Contains("CK_training_work_items_attempt", StringComparison.Ordinal), "Attempt must be pinned to 1 by a check constraint.");
        }

        await using var artifactCommand = connection.CreateCommand();
        artifactCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'training_artifacts';";
        var artifactSql = (string)(await artifactCommand.ExecuteScalarAsync())!;
        AssertEx.True(artifactSql.Contains("CK_training_artifacts_size_bytes", StringComparison.Ordinal), "Artifact size must be non-negative by check constraint.");
    }

    [Test]
    public async Task AddTrainingRunsMigration_LeavesNoPendingModelChanges()
    {
        _ = Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "snapshot.sqlite");
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync();

        // A stale NodeChatDbContextModelSnapshot is the classic missed step: the migration applies but the model no
        // longer matches it, so the next migration would silently re-create these tables.
        AssertEx.False(context.Database.HasPendingModelChanges(), "The model snapshot must be in sync with the applied migrations.");
    }
}
