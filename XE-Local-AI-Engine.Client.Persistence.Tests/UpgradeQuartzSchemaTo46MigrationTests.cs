namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>UpgradeQuartzSchemaTo46</c> migration: a 3.x-shaped QRTZ_ schema with a stored job and cron trigger
///     gains the Quartz 4.6 columns and tables, keeps its rows and its cascade delete, and rolls back to the 3.x shape.
/// </summary>
/// <remarks>That Quartz 4 accepts the result is proved by the scheduler start under schema validation in NodeSchedulerRegistrationTests.</remarks>
[Category(TestCategories.Integration)]
public sealed class UpgradeQuartzSchemaTo46MigrationTests : IDisposable
{
    private const string PreviousMigrationId = "20261021204345_AddBenchmarkRunAgentDefinition";
    private const string QuartzMigrationId = "20261030160543_UpgradeQuartzSchemaTo46";

    private static readonly string[] AddedTriggerColumns =
    [
        "RETRY_POLICY", "RETRY_ATTEMPT", "CONTINUES_TRIGGER_NAME", "CONTINUES_TRIGGER_GROUP", "CONTINUATION_CONDITION",
        "OVERLAP_POLICY", "PAUSE_REASON", "PAUSED_BY", "PAUSED_AT", "MISFIRE_THRESHOLD"
    ];

    private static readonly string[] AddedTables = ["QRTZ_PAUSED_JOB_GRPS", "QRTZ_EXECUTION_HISTORY", "QRTZ_MISFIRE_HISTORY", "QRTZ_JOB_STATUS"];

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
    public async Task MigrateAsync_FromPreviousMigration_AddsThe46SchemaAndKeepsStoredJobsAndTheirCascade()
    {
        var databasePath = GetDatabasePath("quartz-46-up.sqlite");
        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, PreviousMigrationId);
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            await InsertCronJobAsync(connection);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(QuartzMigrationId);
        }

        await using var migrated = await OpenConnectionAsync(databasePath);
        var triggerColumns = await GetColumnsAsync(migrated, "QRTZ_TRIGGERS");
        AssertEx.Empty(AddedTriggerColumns.Except(triggerColumns, StringComparer.Ordinal), "Every Quartz 4.x trigger column must be added.");
        AssertEx.True((await GetColumnsAsync(migrated, "QRTZ_FIRED_TRIGGERS")).Contains("INTERRUPT_REQUESTED_TIME"),
            "QRTZ_FIRED_TRIGGERS must gain INTERRUPT_REQUESTED_TIME.");
        AssertEx.True((await GetColumnsAsync(migrated, "QRTZ_SCHEDULER_STATE")).Contains("QUARTZ_VERSION"),
            "QRTZ_SCHEDULER_STATE must gain QUARTZ_VERSION.");
        foreach (var table in AddedTables)
        {
            AssertEx.True((await GetColumnsAsync(migrated, table)).Count > 0, $"{table} must exist after the migration.");
        }

        AssertEx.Equal(1L, await CountAsync(migrated, "QRTZ_JOB_DETAILS"), "The stored job must survive the migration.");

        // The renamed cascade trigger must still remove a deleted trigger's cron row.
        await ExecuteAsync(migrated, "DELETE FROM QRTZ_TRIGGERS;");
        AssertEx.Equal(0L, await CountAsync(migrated, "QRTZ_CRON_TRIGGERS"), "Deleting a trigger must cascade to its cron row.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_RestoresThe3xSchema()
    {
        var databasePath = GetDatabasePath("quartz-46-down.sqlite");
        await MigratedDatabaseTemplate.CopyChatAtAsync(databasePath, QuartzMigrationId);

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigrationId);
        }

        await using var connection = await OpenConnectionAsync(databasePath);
        var triggerColumns = await GetColumnsAsync(connection, "QRTZ_TRIGGERS");
        AssertEx.Empty(AddedTriggerColumns.Intersect(triggerColumns, StringComparer.Ordinal), "Rollback must drop every 4.x trigger column.");
        AssertEx.True(triggerColumns.Contains("PREFERRED_NODE"), "Rollback must keep the 3.x columns.");
        foreach (var table in AddedTables)
        {
            AssertEx.Empty(await GetColumnsAsync(connection, table), $"Rollback must drop {table}.");
        }

        await InsertCronJobAsync(connection);
        await ExecuteAsync(connection, "DELETE FROM QRTZ_TRIGGERS;");
        AssertEx.Equal(0L, await CountAsync(connection, "QRTZ_CRON_TRIGGERS"), "The restored 3.x cascade trigger must still fire.");
    }

    private static async Task InsertCronJobAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            INSERT INTO QRTZ_JOB_DETAILS (SCHED_NAME, JOB_NAME, JOB_GROUP, JOB_CLASS_NAME, IS_DURABLE, IS_NONCONCURRENT, IS_UPDATE_DATA, REQUESTS_RECOVERY)
            VALUES ('s', 'job', 'g', 'Job, Assembly', 1, 0, 0, 0);
            INSERT INTO QRTZ_TRIGGERS (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP, JOB_NAME, JOB_GROUP, TRIGGER_STATE, TRIGGER_TYPE, START_TIME)
            VALUES ('s', 'trigger', 'g', 'job', 'g', 'WAITING', 'CRON', 0);
            INSERT INTO QRTZ_CRON_TRIGGERS (SCHED_NAME, TRIGGER_NAME, TRIGGER_GROUP, CRON_EXPRESSION)
            VALUES ('s', 'trigger', 'g', '0 0 * * * ?');
            """);
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Every caller passes a compile-time test constant, never user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // A table name cannot be bound as a parameter; every caller passes a QRTZ_ constant.
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
#pragma warning restore CA2100
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<string>> GetColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        _ = command.Parameters.AddWithValue("$table", table);
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
