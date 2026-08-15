namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddTrainingEvaluationMigrationTests : IDisposable
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
    public async Task AddTrainingEvaluationMigration_RoundTrips()
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
                     "training_evaluation_runs",
                     "training_comparison_reports"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            command.Parameters.AddWithValue("$name", table);
            AssertEx.Equal(expected: 1L, (long)(await command.ExecuteScalarAsync())!, $"Migration should create {table}.");
        }

        await using (var evaluationCommand = connection.CreateCommand())
        {
            evaluationCommand.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'training_evaluation_runs';";
            var sql = (string)(await evaluationCommand.ExecuteScalarAsync())!;
            AssertEx.True(sql.Contains("CK_training_evaluation_runs_counts", StringComparison.Ordinal),
                "The aggregate columns must be bounded by a check constraint, not by writer discipline alone.");
        }

        // The three indexes the evaluation surface actually queries by: run lineage, report binding, and the status
        // filter the list uses.
        foreach (var index in new[]
                 {
                     "ix_training_evaluation_runs_training_run",
                     "ix_training_evaluation_runs_comparison",
                     "ix_training_evaluation_runs_status",
                     "ix_training_comparison_reports_training_run"
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
            command.Parameters.AddWithValue("$name", index);
            AssertEx.Equal(expected: 1L, (long)(await command.ExecuteScalarAsync())!, $"Migration should create {index}.");
        }
    }

    [Test]
    public async Task AddTrainingEvaluationMigration_LeavesNoPendingModelChanges()
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
