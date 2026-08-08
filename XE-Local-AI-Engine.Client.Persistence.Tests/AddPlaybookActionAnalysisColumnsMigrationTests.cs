namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddPlaybookActionAnalysisColumnsMigrationTests : IDisposable
{
    private const string PrePlaybookAnalysisMigrationId = "20260531061240_AddPlaybookActions";
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
    public async Task MigrateAsync_AddsNullableSourceFeedbackIdsAndConfidenceColumns()
    {
        var databasePath = GetDatabasePath("analysis-columns-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePlaybookAnalysisMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetPlaybookActionColumnInfoAsync(connection).ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("source_feedback_ids"), "Migration should add the source_feedback_ids column.");
        AssertEx.True(columns.ContainsKey("confidence"), "Migration should add the confidence column.");
        AssertEx.False(columns["source_feedback_ids"], "source_feedback_ids should be nullable.");
        AssertEx.False(columns["confidence"], "confidence should be nullable.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsSourceFeedbackIdsAndConfidenceColumns()
    {
        var databasePath = GetDatabasePath("analysis-columns-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PrePlaybookAnalysisMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetPlaybookActionColumnInfoAsync(connection).ConfigureAwait(false);

        AssertEx.False(columns.ContainsKey("source_feedback_ids"), "Rollback should drop the source_feedback_ids column.");
        AssertEx.False(columns.ContainsKey("confidence"), "Rollback should drop the confidence column.");
        // The pre-analysis playbook_actions schema must survive the rollback intact.
        AssertEx.True(columns.ContainsKey("behavior"), "Rollback should retain the original playbook_actions schema.");
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

    private static async Task<IReadOnlyDictionary<string, bool>> GetPlaybookActionColumnInfoAsync(SqliteConnection connection)
    {
        // PRAGMA table_info exposes the per-column NOT NULL flag, which lets the test assert the new columns are nullable.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(playbook_actions);";

        var columns = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            var notNull = reader.GetInt64(reader.GetOrdinal("notnull")) != 0L;
            columns[name] = notNull;
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
