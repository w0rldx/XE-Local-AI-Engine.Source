namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddPlaybookActionEnabledAtUtcMigrationTests : IDisposable
{
    private const string PreEnabledAtUtcMigrationId = "20260531105623_AddPlaybookEvalAndGoldenConversations";
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
    public async Task MigrateAsync_AddsNullableEnabledAtUtcColumn()
    {
        var databasePath = GetDatabasePath("enabled-at-utc-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreEnabledAtUtcMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetPlaybookActionColumnInfoAsync(connection).ConfigureAwait(false);

        AssertEx.True(columns.ContainsKey("enabled_at_utc"), "Migration should add the enabled_at_utc column.");
        AssertEx.False(columns["enabled_at_utc"], "enabled_at_utc should be nullable.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsEnabledAtUtcColumn()
    {
        var databasePath = GetDatabasePath("enabled-at-utc-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreEnabledAtUtcMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetPlaybookActionColumnInfoAsync(connection).ConfigureAwait(false);

        AssertEx.False(columns.ContainsKey("enabled_at_utc"), "Rollback should drop the enabled_at_utc column.");
        // The earlier playbook_actions schema (before enabled_at_utc was added) must survive the rollback intact.
        AssertEx.True(columns.ContainsKey("behavior"), "Rollback should retain the original playbook_actions schema.");
        AssertEx.True(columns.ContainsKey("eval_result"), "Rollback should retain the eval_result column.");
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
        // PRAGMA table_info exposes the per-column NOT NULL flag, which lets the test assert the new column is nullable.
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
