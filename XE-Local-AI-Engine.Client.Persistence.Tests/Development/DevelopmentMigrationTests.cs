namespace XE_Local_AI_Engine.Client.Persistence.Tests.Development;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class DevelopmentMigrationTests : IDisposable
{
    private const string PreDevelopmentMigrationId = "20260718143054_AddAgentExecutionLogProvider";

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Migration_AppliesExactlyFiveDevelopmentTablesAndRequiredIndexesThenRollsBack()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "development.sqlite");
        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDevelopmentMigrationId).ConfigureAwait(false);
            await context.Database.MigrateAsync().ConfigureAwait(false);
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            AssertEx.True((await NamesAsync(connection, "table", "development_%").ConfigureAwait(false)).SetEquals([
                "development_artifacts",
                "development_attempts",
                "development_events",
                "development_projects",
                "development_tasks"
            ]));
            var indexes = await NamesAsync(connection, "index", "ux_development_%").ConfigureAwait(false);
            AssertEx.True(indexes.Contains("ux_development_attempts_one_active_per_task"));
            AssertEx.True(indexes.Contains("ux_development_events_project_sequence"));
            AssertEx.True(indexes.Contains("ux_development_events_operation_phase"));
        }

        await using (var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder))
        {
            await context.Database.GetService<IMigrator>().MigrateAsync(PreDevelopmentMigrationId).ConfigureAwait(false);
        }

        await using var rolledBack = new SqliteConnection($"Data Source={databasePath}");
        await rolledBack.OpenAsync().ConfigureAwait(false);
        AssertEx.Empty(await NamesAsync(rolledBack, "table", "development_%").ConfigureAwait(false));
    }

    private static async Task<HashSet<string>> NamesAsync(SqliteConnection connection, string type, string pattern)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = $type AND name LIKE $pattern ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$pattern", pattern);
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
