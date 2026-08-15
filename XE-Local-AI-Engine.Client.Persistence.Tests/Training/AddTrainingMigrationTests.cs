namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddTrainingMigrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddTrainingMigration_RoundTrips()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "migration.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        foreach (var table in new[]
                 {
                     "training_dataset_definitions",
                     "training_datasets",
                     "training_dataset_samples",
                     "tool_mock_definitions",
                     "training_base_artifacts",
                     "dataset_generation_work_items"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            AssertEx.Equal(expected: 1L, (long)(await command.ExecuteScalarAsync())!, $"Migration should create {table}.");
        }

        await using var sequenceCommand = connection.CreateCommand();
        sequenceCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'dataset_generation_work_items';";
        var sql = (string)(await sequenceCommand.ExecuteScalarAsync())!;
        AssertEx.True(sql.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase), "Queue sequence must be durable SQLite AUTOINCREMENT.");
    }

    [Test]
    public async Task AddTrainingMigration_LeavesNoPendingModelChanges()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "snapshot.sqlite");
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync();

        // A stale NodeChatDbContextModelSnapshot is the classic missed step: the migration applies but the model no
        // longer matches it, so the next migration would silently re-create these tables.
        AssertEx.False(context.Database.HasPendingModelChanges(), "The model snapshot must be in sync with the applied migrations.");
    }
}
