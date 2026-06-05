namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddSchedulerTablesMigrationTests : IDisposable
{
    private const string PreSchedulerMigrationId = "20260601085538_AddGoldenConversationHarvestProvenance";

    private static readonly string[] ExpectedQrtzTables =
    [
        "QRTZ_JOB_DETAILS",
        "QRTZ_TRIGGERS",
        "QRTZ_SIMPLE_TRIGGERS",
        "QRTZ_SIMPROP_TRIGGERS",
        "QRTZ_CRON_TRIGGERS",
        "QRTZ_BLOB_TRIGGERS",
        "QRTZ_CALENDARS",
        "QRTZ_PAUSED_TRIGGER_GRPS",
        "QRTZ_FIRED_TRIGGERS",
        "QRTZ_SCHEDULER_STATE",
        "QRTZ_LOCKS"
    ];

    private static readonly string[] ExpectedDeleteTriggers =
    [
        "DELETE_SIMPLE_TRIGGER",
        "DELETE_SIMPROP_TRIGGER",
        "DELETE_CRON_TRIGGER",
        "DELETE_BLOB_TRIGGER"
    ];

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
    public async Task MigrateAsync_WhenApplied_CreatesAllThreeSchedulerAppTables()
    {
        var databasePath = GetDatabasePath("scheduler-app-tables-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreSchedulerMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "scheduled_job_definitions").ConfigureAwait(false),
            "Migration should create the scheduled_job_definitions table.");
        AssertEx.True(await TableExistsAsync(connection, "scheduled_job_runs").ConfigureAwait(false),
            "Migration should create the scheduled_job_runs table.");
        AssertEx.True(await TableExistsAsync(connection, "scheduled_job_run_events").ConfigureAwait(false),
            "Migration should create the scheduled_job_run_events table.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_CreatesAllElevenQrtzTables()
    {
        var databasePath = GetDatabasePath("scheduler-qrtz-tables-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreSchedulerMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        foreach (var tableName in ExpectedQrtzTables)
        {
            AssertEx.True(await TableExistsAsync(connection, tableName).ConfigureAwait(false),
                $"Migration should create the {tableName} table.");
        }
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_CreatesDeleteTriggers()
    {
        var databasePath = GetDatabasePath("scheduler-triggers-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreSchedulerMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        foreach (var triggerName in ExpectedDeleteTriggers)
        {
            AssertEx.True(await TriggerExistsAsync(connection, triggerName).ConfigureAwait(false),
                $"Migration should create the {triggerName} trigger.");
        }
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_ScheduledJobDefinitionsHasExpectedColumns()
    {
        var databasePath = GetDatabasePath("scheduler-def-columns-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreSchedulerMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetTableColumnsAsync(connection, "scheduled_job_definitions").ConfigureAwait(false);

        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "template_id",
            "display_name",
            "description",
            "enabled",
            "schedule_kind",
            "cron_expression",
            "interval_seconds",
            "repeat_count",
            "start_at_utc",
            "end_at_utc",
            "time_zone_id",
            "misfire_policy",
            "prevent_overlap",
            "max_runtime_seconds",
            "parameter_json",
            "created_by",
            "created_at_utc",
            "updated_at_utc",
            "disabled_at_utc",
            "deleted_at_utc"
        }), "scheduled_job_definitions should expose all mapped columns.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_ScheduledJobRunsHasExpectedColumns()
    {
        var databasePath = GetDatabasePath("scheduler-runs-columns-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreSchedulerMigrationId).ConfigureAwait(false);
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        var columns = await GetTableColumnsAsync(connection, "scheduled_job_runs").ConfigureAwait(false);

        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "scheduled_job_id",
            "template_id",
            "quartz_fire_instance_id",
            "triggered_by",
            "status",
            "scheduled_fire_time_utc",
            "actual_fire_time_utc",
            "completed_at_utc",
            "duration_ms",
            "summary",
            "details_json",
            "error_message",
            "error_details",
            "cancellation_requested_at_utc",
            "created_at_utc"
        }), "scheduled_job_runs should expose all mapped columns.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsAllSchedulerTablesAndQrtzTables()
    {
        var databasePath = GetDatabasePath("scheduler-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreSchedulerMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "scheduled_job_definitions").ConfigureAwait(false),
            "Rollback should drop scheduled_job_definitions.");
        AssertEx.False(await TableExistsAsync(connection, "scheduled_job_runs").ConfigureAwait(false),
            "Rollback should drop scheduled_job_runs.");
        AssertEx.False(await TableExistsAsync(connection, "scheduled_job_run_events").ConfigureAwait(false),
            "Rollback should drop scheduled_job_run_events.");

        foreach (var tableName in ExpectedQrtzTables)
        {
            AssertEx.False(await TableExistsAsync(connection, tableName).ConfigureAwait(false),
                $"Rollback should drop {tableName}.");
        }
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

    private static async Task<bool> TriggerExistsAsync(SqliteConnection connection, string triggerName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name = $name;";
        command.Parameters.AddWithValue("$name", triggerName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<IReadOnlySet<string>> GetTableColumnsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        // tableName is an internal test constant, never user input.
#pragma warning disable CA2100
        command.CommandText = $"SELECT * FROM {tableName} LIMIT 0;";
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
