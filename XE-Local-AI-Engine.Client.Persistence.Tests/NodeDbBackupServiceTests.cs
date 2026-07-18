namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

public sealed class NodeDbBackupServiceTests : IDisposable
{
    private const string BackupFilePrefix = "node-chat-";
    private const string BackupFileExtension = ".sqlite";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_WhenMigrationsPending_CreatesSnapshot()
    {
        var databasePath = GetDatabasePath("pending.sqlite");
        await SeedRawDataAsync(databasePath).ConfigureAwait(false);

        await using var serviceProvider = BuildServiceProvider(databasePath);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        await backupService.BackupBeforeMigrationAsync();

        var snapshots = ListSnapshots();
        AssertEx.Equal(1, snapshots.Length, "Exactly one snapshot should be written when migrations are pending.");
        AssertEx.True(new FileInfo(snapshots[0]).Length > 0, "The snapshot should be a non-empty SQLite file.");
        AssertEx.True(await ProbeTableExistsAsync(snapshots[0]).ConfigureAwait(false), "The snapshot should contain the seeded source data.");
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_WhenNoMigrationsPending_DoesNotCreateSnapshot()
    {
        var databasePath = GetDatabasePath("up-to-date.sqlite");

        await using var serviceProvider = BuildServiceProvider(databasePath);

        // Apply every migration first so the node database is fully up to date — nothing pending, so nothing to back up.
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();
        await backupService.BackupBeforeMigrationAsync();

        AssertEx.Empty(ListSnapshots(), "No snapshot should be written when there are no pending migrations.");
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_PrunesToRetainCount()
    {
        var databasePath = GetDatabasePath("prune.sqlite");
        await SeedRawDataAsync(databasePath).ConfigureAwait(false);

        var backupDirectory = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(backupDirectory);

        // Four pre-existing snapshots, all older than the fake clock so the fresh one sorts newest.
        var older = new[] { "20250101T000000000Z", "20250102T000000000Z", "20250103T000000000Z", "20250104T000000000Z" };
        foreach (var stamp in older)
        {
            await File.WriteAllTextAsync(Path.Combine(backupDirectory, $"{BackupFilePrefix}{stamp}{BackupFileExtension}"), "stale").ConfigureAwait(false);
        }

        // Fixed clock at 2026-01-01T00:00:00Z → the new snapshot's timestamp sorts after all four pre-existing ones.
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var serviceProvider = BuildServiceProvider(databasePath, retainCount: 3, timeProvider: clock);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        await backupService.BackupBeforeMigrationAsync();

        var remaining = ListSnapshots().Select(Path.GetFileName).OrderDescending(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(3, remaining.Length, "Retention should cap the snapshot count at RetainCount.");
        AssertEx.Equal($"{BackupFilePrefix}20260101T000000000Z{BackupFileExtension}", remaining[0], "The freshest snapshot should be retained.");
        AssertEx.Equal($"{BackupFilePrefix}20250104T000000000Z{BackupFileExtension}", remaining[1], "The two newest pre-existing snapshots should be retained.");
        AssertEx.Equal($"{BackupFilePrefix}20250103T000000000Z{BackupFileExtension}", remaining[2], "The two newest pre-existing snapshots should be retained.");
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_WhenBackupFails_SwallowsAndDoesNotThrow()
    {
        var databasePath = GetDatabasePath("failure.sqlite");
        await SeedRawDataAsync(databasePath).ConfigureAwait(false);

        // Plant a FILE where the backup directory is expected: Directory.CreateDirectory then throws IOException, exercising
        // the swallow-and-continue failure policy.
        var collidingPath = Path.Combine(_rootPath, "backup-collision");
        await File.WriteAllTextAsync(collidingPath, "not a directory").ConfigureAwait(false);

        await using var serviceProvider = BuildServiceProvider(databasePath, backupDirectoryOverride: collidingPath);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        // Must return normally — a backup failure never blocks migration/startup. (An exception here fails the test.)
        await backupService.BackupBeforeMigrationAsync();

        AssertEx.True(File.Exists(collidingPath), "The colliding file should be left untouched.");
        AssertEx.False(Directory.Exists(collidingPath), "The failed backup must not have replaced the file with a directory.");
    }

    private ServiceProvider BuildServiceProvider(string databasePath,
        int retainCount = 3,
        string? backupDirectoryOverride = null,
        TimeProvider? timeProvider = null)
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
        services.AddSingleton<INodeDataDirectory>(new FixedNodeDataDirectory(_rootPath));
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite(connectionString));
        services.AddOptions<NodeDbBackupOptions>()
                .Configure(options =>
                {
                    options.RetainCount = retainCount;
                    options.BackupDirectory = backupDirectoryOverride;
                });
        services.AddSingleton<INodeDbBackupService, NodeDbBackupService>();

        return services.BuildServiceProvider(true);
    }

    private string[] ListSnapshots()
    {
        var backupDirectory = Path.Combine(_rootPath, "backups");
        return Directory.Exists(backupDirectory)
            ? Directory.GetFiles(backupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}")
            : [];
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    // Writes a small user table into a plain SQLite file so the source database is non-empty AND still reports every EF
    // migration as pending (no __EFMigrationsHistory table exists yet).
    private static async Task SeedRawDataAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE "probe" ("Id" INTEGER NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL);
                              INSERT INTO "probe" ("Id", "Value") VALUES (1, 'seed');
                              """;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> ProbeTableExistsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'probe';";
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private sealed class FixedNodeDataDirectory(string root) : INodeDataDirectory
    {
        public string Root { get; } = root;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }
}
