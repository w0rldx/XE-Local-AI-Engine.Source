namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The <c>trained</c> origin has to be admitted by BOTH the hand-written EF converter pair on
///     <c>BenchmarkRunConfiguration</c> and the database CHECK constraint. Missing either one turns the first benchmark
///     run of a locally trained model into a throw at save time.
/// </summary>
public sealed class AddTrainedModelOriginMigrationTests : IDisposable
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
    public async Task AddTrainedModelOriginMigration_WidensTheCheckConstraint()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "migration.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync();
        }

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'benchmark_runs';";
        var sql = (string)(await command.ExecuteScalarAsync())!;

        AssertEx.True(sql.Contains("'trained'", StringComparison.Ordinal),
            "The origin CHECK constraint must admit a trained model.");
        AssertEx.True(sql.Contains("'huggingface'", StringComparison.Ordinal) && sql.Contains("'imported'", StringComparison.Ordinal),
            "Widening the constraint must not drop the origins it already admitted.");
    }

    [Test]
    public async Task AddTrainedModelOriginMigration_LeavesNoPendingModelChanges()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "snapshot.sqlite");
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync();

        AssertEx.False(context.Database.HasPendingModelChanges(), "The model snapshot must be in sync with the applied migrations.");
    }

    [Test]
    public async Task BenchmarkRunConfig_OriginConverter_AcceptsTrained()
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, "converter.sqlite");
        var projectId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync();
            _ = context.BenchmarkProjects.Add(new BenchmarkProject
            {
                Id = projectId,
                Name = "trained-origin",
                CoreTaskJson = "{}"u8.ToArray(),
                ContextTokens = 4096,
                AgentDefinitionId = Guid.NewGuid(),
                Version = 1
            });
            _ = context.BenchmarkRuns.Add(new BenchmarkRun
            {
                Id = runId,
                ProjectId = projectId,
                RuntimeSnapshotJson = "{}"u8.ToArray(),
                PrimaryModelName = "local/tuned:Q4_K_M",
                PrimaryModelOrigin = LocalModelOrigin.Trained,
                ModelContentFingerprint = "v1:" + new string('a', 64),
                AgentName = "agent",
                RequestedContextTokens = 4096,
                Version = 1
            });

            // Both the converter's write direction and the CHECK constraint fire here.
            _ = await context.SaveChangesAsync();
        }

        await using var reader = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        var stored = await reader.BenchmarkRuns.AsNoTracking().SingleAsync(run => run.Id == runId);

        AssertEx.Equal(LocalModelOrigin.Trained, stored.PrimaryModelOrigin);
    }
}
