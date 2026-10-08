namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

/// <summary>
///     Serialized against the rest of the module: every test applies the whole migration set against a real SQLite
///     file, and a starved run used to be cancelled mid-apply by the old wall-clock attempt budget.
/// </summary>
[NotInParallel]
[Category(TestCategories.Integration)]
public sealed class NodeChatMigrationRecoveryServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task MigrateAsync_WhenNoAbandonedLock_AppliesNodeChatMigrations()
    {
        var databasePath = GetDatabasePath("normal.sqlite");

        await using var serviceProvider = BuildServiceProvider(databasePath);
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        await migrationService.MigrateAsync();

        await using var connection = await OpenConnectionAsync(databasePath);

        AssertEx.True(await TableExistsAsync(connection, "__EFMigrationsHistory"), "Migrations history table should be created.");
        AssertEx.True(await TableExistsAsync(connection, "conversations"), "Node conversations table should be created.");
        AssertEx.True(await MigrationLockIsEmptyOrMissingAsync(connection), "Successful migrations should not leave an active EF migrations lock row behind.");
    }

    [Test]
    public async Task MigrateAsync_WhenEfMigrationsLockIsAbandoned_DropsLockAndAppliesMigrations()
    {
        var databasePath = GetDatabasePath("abandoned-lock.sqlite");
        await CreateAbandonedEfMigrationLockAsync(databasePath);

        await using var serviceProvider = BuildServiceProvider(databasePath);
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        await migrationService.MigrateAsync();

        await using var connection = await OpenConnectionAsync(databasePath);

        AssertEx.True(await TableExistsAsync(connection, "__EFMigrationsHistory"), "Migrations should succeed after stale lock cleanup.");
        AssertEx.True(await TableExistsAsync(connection, "messages"), "Node messages table should be created after retry.");
        AssertEx.True(await MigrationLockIsEmptyOrMissingAsync(connection), "Recovered migrations should clear the abandoned lock row.");
    }

    [Test]
    public async Task MigrateAsync_WhenStartupLockIsHeld_ThrowsWithoutDroppingEfLockTable()
    {
        var databasePath = GetDatabasePath("held-startup-lock.sqlite");
        await CreateAbandonedEfMigrationLockAsync(databasePath);
        using var lockFile = new FileStream(databasePath + ".migration.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await using var serviceProvider = BuildServiceProvider(databasePath, startupLockTimeout: TimeSpan.FromMilliseconds(25));
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        var exception = await ThrowsAsync<InvalidOperationException>(() => migrationService.MigrateAsync());

        AssertEx.True(exception.Message.Contains("migration startup lock", StringComparison.OrdinalIgnoreCase));

        await using var connection = await OpenConnectionAsync(databasePath);
        AssertEx.True(await TableExistsAsync(connection, "__EFMigrationsLock"), "EF lock table should remain untouched when startup ownership is not acquired.");
    }

    [Test]
    public async Task MigrateAsync_RunsTheMigrationWithoutAWallClockBudget()
    {
        var databasePath = GetDatabasePath("unbounded.sqlite");
        var recorder = new MigrationCancellationRecorder();

        await using var serviceProvider = BuildServiceProvider(databasePath, interceptor: recorder);
        var migrationService = serviceProvider.GetRequiredService<NodeChatMigrationRecoveryService>();

        await migrationService.MigrateAsync();

        // A slow but healthy migration (a table rebuild on a large database) must never be cancelled mid-apply; only the caller may stop it.
        AssertEx.True(recorder.MigrationCommandCount > 0, "The probe must observe the migration's own commands.");
        AssertEx.False(recorder.AnyMigrationCommandCancelable, "Migration commands must run under the caller's token, not a timeout.");
    }

    private static ServiceProvider BuildServiceProvider(string databasePath,
        TimeSpan? startupLockTimeout = null,
        IInterceptor? interceptor = null)
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
        services.AddDbContext<NodeChatDbContext>(options =>
        {
            options.UseSqlite(connectionString);
            if (interceptor is not null)
            {
                options.AddInterceptors(interceptor);
            }
        });
        services.AddOptions<NodeChatMigrationRecoveryOptions>()
                .Configure(options =>
                {
                    options.StartupLockTimeout = startupLockTimeout ?? TimeSpan.FromSeconds(1);
                    options.StartupLockPollInterval = TimeSpan.FromMilliseconds(5);
                });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<NodeChatMigrationRecoveryService>();

        return services.BuildServiceProvider(true);
    }

    private static async Task CreateAbandonedEfMigrationLockAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using var connection = await OpenConnectionAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE "__EFMigrationsLock" (
                                  "Id" INTEGER NOT NULL CONSTRAINT "PK___EFMigrationsLock" PRIMARY KEY,
                                  "Timestamp" TEXT NOT NULL
                              );
                              INSERT INTO "__EFMigrationsLock" ("Id", "Timestamp") VALUES (1, 'stale');
                              """;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private static async Task<bool> MigrationLockIsEmptyOrMissingAsync(SqliteConnection connection)
    {
        if (!await TableExistsAsync(connection, "__EFMigrationsLock"))
        {
            return true;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM \"__EFMigrationsLock\";";

        var result = await command.ExecuteScalarAsync();
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
            await action();
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

    private sealed class MigrationCancellationRecorder : DbCommandInterceptor
    {
        public int MigrationCommandCount { get; private set; }

        public bool AnyMigrationCommandCancelable { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CREATE TABLE \"conversations\"", StringComparison.Ordinal))
            {
                MigrationCommandCount++;
                AnyMigrationCommandCancelable |= cancellationToken.CanBeCanceled;
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
