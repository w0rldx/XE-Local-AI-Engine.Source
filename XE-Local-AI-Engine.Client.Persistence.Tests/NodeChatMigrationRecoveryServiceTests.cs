namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Persistence;

public sealed class NodeChatMigrationRecoveryServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task MigrateAsync_WhenNoAbandonedLock_AppliesNodeChatMigrations()
    {
        var databasePath = GetDatabasePath("normal.sqlite");

        await using var serviceProvider = BuildServiceProvider(databasePath);
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        await migrationService.MigrateAsync();

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "__EFMigrationsHistory").ConfigureAwait(false), "Migrations history table should be created.");
        AssertEx.True(await TableExistsAsync(connection, "conversations").ConfigureAwait(false), "Node conversations table should be created.");
        AssertEx.True(await MigrationLockIsEmptyOrMissingAsync(connection).ConfigureAwait(false), "Successful migrations should not leave an active EF migrations lock row behind.");
    }

    [Test]
    public async Task MigrateAsync_WhenEfMigrationsLockIsAbandoned_DropsLockAndRetriesMigrations()
    {
        var databasePath = GetDatabasePath("abandoned-lock.sqlite");
        await CreateAbandonedEfMigrationLockAsync(databasePath).ConfigureAwait(false);

        await using var serviceProvider = BuildServiceProvider(databasePath, TimeSpan.FromSeconds(5));
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        await migrationService.MigrateAsync();

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "__EFMigrationsHistory").ConfigureAwait(false), "Migrations should succeed after stale lock cleanup.");
        AssertEx.True(await TableExistsAsync(connection, "messages").ConfigureAwait(false), "Node messages table should be created after retry.");
        AssertEx.True(await MigrationLockIsEmptyOrMissingAsync(connection).ConfigureAwait(false), "Recovered migrations should clear the abandoned lock row.");
    }

    [Test]
    public async Task MigrateAsync_WhenStartupLockIsHeld_ThrowsWithoutDroppingEfLockTable()
    {
        var databasePath = GetDatabasePath("held-startup-lock.sqlite");
        await CreateAbandonedEfMigrationLockAsync(databasePath).ConfigureAwait(false);
        using var lockFile = new FileStream(databasePath + ".migration.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await using var serviceProvider = BuildServiceProvider(databasePath, startupLockTimeout: TimeSpan.FromMilliseconds(25));
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        var exception = await ThrowsAsync<InvalidOperationException>(() => migrationService.MigrateAsync()).ConfigureAwait(false);

        AssertEx.True(exception.Message.Contains("migration startup lock", StringComparison.OrdinalIgnoreCase));

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        AssertEx.True(await TableExistsAsync(connection, "__EFMigrationsLock").ConfigureAwait(false), "EF lock table should remain untouched when startup ownership is not acquired.");
    }

    private static ServiceProvider BuildServiceProvider(string databasePath,
        TimeSpan? migrationAttemptTimeout = null,
        TimeSpan? startupLockTimeout = null)
    {
        var connectionString = $"Data Source={databasePath}";
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["ConnectionStrings:node-sqlite"] = connectionString
                            })
                            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite(connectionString));
        services.AddOptions<NodeChatMigrationRecoveryOptions>()
                .Configure(options =>
                {
                    options.MigrationAttemptTimeout = migrationAttemptTimeout ?? TimeSpan.FromSeconds(5);
                    options.StartupLockTimeout = startupLockTimeout ?? TimeSpan.FromSeconds(1);
                    options.StartupLockPollInterval = TimeSpan.FromMilliseconds(5);
                });
        services.AddSingleton<NodeChatMigrationRecoveryService>();

        return services.BuildServiceProvider(true);
    }

    private static async Task CreateAbandonedEfMigrationLockAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE "__EFMigrationsLock" (
                                  "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
                                  "Timestamp" TEXT NOT NULL
                              );
                              INSERT INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, 'stale');
                              """;

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
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
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> MigrationLockIsEmptyOrMissingAsync(SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "__EFMigrationsLock").ConfigureAwait(false))
        {
            return true;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";

        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) == 0;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new AssertionException($"Expected exception of type {typeof(TException).Name} but caught {exception.GetType().Name}: {exception.Message}");
        }

        throw new AssertionException($"Expected exception of type {typeof(TException).Name} but no exception was thrown.");
    }
}
