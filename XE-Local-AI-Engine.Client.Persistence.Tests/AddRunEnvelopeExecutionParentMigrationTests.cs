namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddRunEnvelopeExecutionParent</c> migration: both parent columns appear on upgrade and on a fresh
///     migrate-to-head, a one-step rollback drops only them, and the model has no snapshot drift.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddRunEnvelopeExecutionParentMigrationTests : IDisposable
{
    private const string KindColumn = "parent_execution_kind";
    private const string IdColumn = "parent_execution_id";

    private const string PreviousMigrationId = "20261019225908_AddBenchmarkReviewsArtifactsToolTiming";

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
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsBothParentColumns()
    {
        var databasePath = GetDatabasePath("execution-parent-up.sqlite");

        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreviousMigrationId);

        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            // The precondition: neither column existed before, so their presence below is this migration's doing.
            var before = await GetEnvelopeColumnNamesAsync(connection);
            AssertEx.False(before.Contains(KindColumn), "agent_execution_logs must not carry parent_execution_kind before this migration.");
            AssertEx.False(before.Contains(IdColumn), "agent_execution_logs must not carry parent_execution_id before this migration.");
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync();
        }

        await using var upgraded = await OpenConnectionAsync(databasePath);
        var after = await GetEnvelopeColumnNamesAsync(upgraded);
        AssertEx.True(after.Contains(KindColumn), "agent_execution_logs should expose parent_execution_kind after the migration.");
        AssertEx.True(after.Contains(IdColumn), "agent_execution_logs should expose parent_execution_id after the migration.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsBothParentColumns()
    {
        var databasePath = GetDatabasePath("execution-parent-fresh.sqlite");

        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        await using var connection = await OpenConnectionAsync(databasePath);
        var columns = await GetEnvelopeColumnNamesAsync(connection);
        AssertEx.True(columns.Contains(KindColumn), "A fresh migrate-to-head should expose parent_execution_kind.");
        AssertEx.True(columns.Contains(IdColumn), "A fresh migrate-to-head should expose parent_execution_id.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsBothParentColumns()
    {
        var databasePath = GetDatabasePath("execution-parent-rollback.sqlite");

        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await using var connection = await OpenConnectionAsync(databasePath);
        var columns = await GetEnvelopeColumnNamesAsync(connection);

        AssertEx.False(columns.Contains(KindColumn), "Rolling back one migration should drop parent_execution_kind.");
        AssertEx.False(columns.Contains(IdColumn), "Rolling back one migration should drop parent_execution_id.");

        // SQLite rebuilds the table to drop a column, from the PRECEDING migration's model: pin the neighbours it must keep.
        AssertEx.True(columns.Contains("model_readiness_ms"), "Rolling back must not disturb the envelope columns that preceded it.");
        AssertEx.True(columns.Contains("dispatched_tier"), "Rolling back must not disturb the envelope columns that preceded it.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("execution-parent-drift.sqlite");
        await using var context = CreateContext(databasePath);

        AssertEx.False(context.Database.HasPendingModelChanges(),
            "The NodeChat model has drifted from the latest migration snapshot — regenerate the migration.");
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

    private static async Task<HashSet<string>> GetEnvelopeColumnNamesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_execution_logs);";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
