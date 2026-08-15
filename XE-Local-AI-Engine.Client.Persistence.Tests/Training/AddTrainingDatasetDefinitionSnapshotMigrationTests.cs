namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddTrainingDatasetDefinitionSnapshotMigrationTests : IDisposable
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
    public async Task AddTrainingDatasetDefinitionSnapshotMigration_AddsANullableBlobColumn()
    {
        _ = Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "migration.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, \"notnull\" FROM pragma_table_info('training_datasets') WHERE name = 'definition_json';";
        await using var reader = await command.ExecuteReaderAsync();

        AssertEx.True(await reader.ReadAsync(), "The migration must add training_datasets.definition_json.");
        AssertEx.Equal("BLOB", reader.GetString(0));

        // Nullable on purpose: an empty-blob NOT NULL default is not decryptable, so every dataset created before
        // pinning would fail materialization instead of reading back as "not pinned".
        AssertEx.Equal(expected: 0L, reader.GetInt64(1), "The column must be nullable so pre-pinning rows stay readable.");
    }

    [Test]
    public async Task AddTrainingDatasetDefinitionSnapshotMigration_LeavesNoPendingModelChanges()
    {
        _ = Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "snapshot.sqlite");
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync();

        AssertEx.False(context.Database.HasPendingModelChanges(), "The model snapshot must be in sync with the applied migrations.");
    }
}
