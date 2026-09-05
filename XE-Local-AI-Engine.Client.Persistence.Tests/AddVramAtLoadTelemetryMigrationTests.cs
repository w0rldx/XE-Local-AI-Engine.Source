namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddVramAtLoadTelemetry</c> migration: it adds the two nullable VRAM columns to
///     <c>dev_workflow_node_runs</c> on an upgrade and on a fresh migrate-to-head, drops both on rollback without
///     disturbing the columns that preceded them, and leaves no model/snapshot drift.
/// </summary>
public sealed class AddVramAtLoadTelemetryMigrationTests : IDisposable
{
    private const string FreeColumn = "vram_free_at_load_bytes";

    private const string AdmittedColumn = "vram_admitted_bytes";

    private const string PreVramMigrationId = "20260904190259_AddModelReadinessTelemetry";

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
    public async Task MigrateAsync_WhenUpgradedFromPreviousMigration_AddsBothVramColumns()
    {
        var databasePath = GetDatabasePath("vram-at-load-up.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreVramMigrationId).ConfigureAwait(false);
        }

        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            // The precondition the rest of this test rests on: the table had neither column before the migration ran,
            // so their presence below is this migration's doing and not the previous one's.
            var before = await GetNodeRunColumnNamesAsync(connection).ConfigureAwait(false);
            AssertEx.False(before.Contains(FreeColumn), "dev_workflow_node_runs must not carry the free-VRAM column before this migration.");
            AssertEx.False(before.Contains(AdmittedColumn), "dev_workflow_node_runs must not carry the admitted-VRAM column before this migration.");
        }

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var upgraded = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var after = await GetNodeRunColumnNamesAsync(upgraded).ConfigureAwait(false);
        AssertEx.True(after.Contains(FreeColumn), "dev_workflow_node_runs should expose the free-VRAM column after the migration.");
        AssertEx.True(after.Contains(AdmittedColumn), "dev_workflow_node_runs should expose the admitted-VRAM column after the migration.");
    }

    [Test]
    public async Task MigrateAsync_ToHeadOnFreshDatabase_AddsBothVramColumns()
    {
        var databasePath = GetDatabasePath("vram-at-load-fresh.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetNodeRunColumnNamesAsync(connection).ConfigureAwait(false);
        AssertEx.True(columns.Contains(FreeColumn), "A fresh migrate-to-head should expose the free-VRAM column.");
        AssertEx.True(columns.Contains(AdmittedColumn), "A fresh migrate-to-head should expose the admitted-VRAM column.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBackOneStep_DropsBothVramColumns()
    {
        var databasePath = GetDatabasePath("vram-at-load-rollback.sqlite");

        await using (var context = CreateContext(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreVramMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetNodeRunColumnNamesAsync(connection).ConfigureAwait(false);

        AssertEx.False(columns.Contains(FreeColumn), "Rolling back one migration should drop the free-VRAM column.");
        AssertEx.False(columns.Contains(AdmittedColumn), "Rolling back one migration should drop the admitted-VRAM column.");

        // SQLite rebuilds a table to drop a column, so a rollback rebuilds from the PRECEDING migration's model — which
        // is exactly how a sibling slice's columns get silently lost. Pin the neighbours this migration sits beside.
        AssertEx.True(columns.Contains("model_readiness_ms"), "Rolling back the VRAM migration must not disturb the column that immediately preceded it.");
        AssertEx.True(columns.Contains("agent_turn_ms"), "Rolling back the VRAM migration must not disturb the cost columns that preceded it.");
        AssertEx.True(columns.Contains("work_session_steps"), "Rolling back the VRAM migration must not disturb the cost columns that preceded it.");
        AssertEx.True(columns.Contains("tool_names_json"), "Rolling back the VRAM migration must not disturb the cost columns that preceded it.");
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        var databasePath = GetDatabasePath("vram-at-load-drift.sqlite");
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
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<HashSet<string>> GetNodeRunColumnNamesAsync(SqliteConnection connection)
    {
        // PRAGMA takes no parameters, so the statement stays a compile-time constant (CA2100), as in the production writer.
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(dev_workflow_node_runs);";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
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
