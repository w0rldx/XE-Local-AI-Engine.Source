namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Persistence;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

[Category(TestCategories.Integration)]
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
        await SeedRawDataAsync(databasePath);

        await using var serviceProvider = BuildServiceProvider(databasePath);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        await backupService.BackupBeforeMigrationAsync();

        var snapshots = ListSnapshots();
        AssertEx.Equal(1, snapshots.Length, "Exactly one snapshot should be written when migrations are pending.");
        AssertEx.True(new FileInfo(snapshots[0]).Length > 0, "The snapshot should be a non-empty SQLite file.");
        AssertEx.True(await ProbeTableExistsAsync(snapshots[0]), "The snapshot should contain the seeded source data.");
        AssertEx.Equal(NodeDbAutomaticBackupOutcome.Succeeded, backupService.LastAutomaticBackup.Outcome);
    }

    [Test]
    public async Task CreateSnapshotAsync_WithNothingPending_WritesAListedSnapshot()
    {
        var databasePath = GetDatabasePath("on-demand.sqlite");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var serviceProvider = BuildServiceProvider(databasePath, timeProvider: clock);
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.MigrateAsync();
        }

        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        var result = await backupService.CreateSnapshotAsync();

        AssertEx.Equal(NodeDbSnapshotCreateStatus.Created, result.Status);
        var snapshot = AssertEx.NotNull(result.Snapshot);
        AssertEx.Equal($"{BackupFilePrefix}20260101T000000000Z{BackupFileExtension}", snapshot.Name);
        AssertEx.Equal(clock.GetUtcNow(), snapshot.CreatedUtc);
        AssertEx.Equal(snapshot.Name, backupService.ListSnapshots().Single().Name);
        AssertEx.NotNull(backupService.ResolveSnapshotPath(snapshot.Name));
        AssertEx.Equal(NodeDbAutomaticBackupOutcome.NotRun, backupService.LastAutomaticBackup.Outcome, "An on-demand snapshot is not the automatic one.");
    }

    [Test]
    public async Task StageRestoreAsync_WhileASnapshotIsBeingTaken_IsBusy_AndStagesAfterwards()
    {
        var databasePath = GetDatabasePath("stage-busy.sqlite");
        await SeedRawDataAsync(databasePath);
        var gate = new SnapshotGate();
        await using var serviceProvider = BuildServiceProvider(databasePath, chatInterceptor: gate);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        var creating = backupService.CreateSnapshotAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var whileCreating = await backupService.StageRestoreAsync($"{BackupFilePrefix}20260101T000000000Z{BackupFileExtension}");
        gate.Release.SetResult();
        var created = await creating;

        AssertEx.Equal(NodeDbRestoreStageStatus.Busy, whileCreating.Status);
        AssertEx.False(File.Exists(Path.Combine(_rootPath, "backups", NodeDbRestoreStaging.MarkerFileName)), "A busy stage writes no marker.");

        var staged = await backupService.StageRestoreAsync(created.Snapshot!.Name);
        AssertEx.Equal(NodeDbRestoreStageStatus.Staged, staged.Status, staged.Reason);
        AssertEx.Equal(created.Snapshot.Name, NodeDbRestoreStaging.ReadStagedSnapshotName(Path.Combine(_rootPath, "backups")));
    }

    [Test]
    public async Task CreateSnapshotAsync_PruneKeepsTheSnapshotAStagedRestoreNames()
    {
        var databasePath = GetDatabasePath("prune-staged.sqlite");
        await SeedRawDataAsync(databasePath);
        var backupDirectory = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        foreach (var stamp in new[]
                 {
                     "20250101T000000000Z",
                     "20250102T000000000Z",
                     "20250103T000000000Z"
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(backupDirectory, $"{BackupFilePrefix}{stamp}{BackupFileExtension}"), "older");
        }

        var oldest = $"{BackupFilePrefix}20250101T000000000Z{BackupFileExtension}";
        await NodeDbRestoreStaging.WriteMarkerAsync(backupDirectory, oldest, DateTimeOffset.UnixEpoch, CancellationToken.None);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var serviceProvider = BuildServiceProvider(databasePath, retainCount: 1, timeProvider: clock);

        await serviceProvider.GetRequiredService<INodeDbBackupService>().CreateSnapshotAsync();

        var remaining = ListSnapshots().Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Equal($"{oldest},{BackupFilePrefix}20260101T000000000Z{BackupFileExtension}", string.Join(',', remaining),
            "Retention keeps the newest one plus the snapshot the staged restore names.");
    }

    [Test]
    public async Task CreateSnapshotAsync_WhenFreeSpaceTooLow_ReportsItAndWritesNothing()
    {
        var databasePath = GetDatabasePath("on-demand-low-space.sqlite");
        await SeedRawDataAsync(databasePath);
        var freeSpace = Substitute.For<IFreeSpaceProbe>();
        freeSpace.GetAvailableFreeBytes(Arg.Any<string>()).Returns(1L);
        await using var serviceProvider = BuildServiceProvider(databasePath, freeSpaceProbe: freeSpace);

        var result = await serviceProvider.GetRequiredService<INodeDbBackupService>().CreateSnapshotAsync();

        AssertEx.Equal(NodeDbSnapshotCreateStatus.InsufficientSpace, result.Status);
        AssertEx.Empty(ListSnapshots());
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_WhenNoMigrationsPending_DoesNotCreateSnapshot()
    {
        var databasePath = GetDatabasePath("up-to-date.sqlite");

        await using var serviceProvider = BuildServiceProvider(databasePath);

        // Apply every migration of both contexts first so the node database is fully up to date — nothing pending, so nothing to back up.
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.MigrateAsync();
            await scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>().Database.MigrateAsync();
        }

        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();
        await backupService.BackupBeforeMigrationAsync();

        AssertEx.Empty(ListSnapshots(), "No snapshot should be written when there are no pending migrations.");
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_WhenOnlyIdentityMigrationsPending_CreatesSnapshot()
    {
        var databasePath = GetDatabasePath("identity-pending.sqlite");

        await using var serviceProvider = BuildServiceProvider(databasePath);
        await using (var scope = serviceProvider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.MigrateAsync();
        }

        await serviceProvider.GetRequiredService<INodeDbBackupService>().BackupBeforeMigrationAsync();

        AssertEx.Equal(1, ListSnapshots().Length, "Identity migrations share the SQLite file, so they must be snapshotted too.");
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_SweepsKilledSnapshotLeftoversWithoutCountingThem()
    {
        var databasePath = GetDatabasePath("leftovers.sqlite");
        await SeedRawDataAsync(databasePath);

        var backupDirectory = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(backupDirectory);
        foreach (var stamp in new[]
                 {
                     "20250102T000000000Z",
                     "20250103T000000000Z",
                     "20250104T000000000Z"
                 })
        {
            await File.WriteAllTextAsync(Path.Combine(backupDirectory, $"{BackupFilePrefix}{stamp}{BackupFileExtension}"), "good");
        }

        // What a killed or disk-full VACUUM INTO leaves behind: both sort newer than every good snapshot.
        var emptySnapshot = Path.Combine(backupDirectory, $"{BackupFilePrefix}20270101T000000000Z{BackupFileExtension}");
        await File.WriteAllBytesAsync(emptySnapshot, []);
        var partialTemp = Path.Combine(backupDirectory, $"{BackupFilePrefix}20270102T000000000Z{BackupFileExtension}.tmp");
        await File.WriteAllTextAsync(partialTemp, "partial");

        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var serviceProvider = BuildServiceProvider(databasePath, retainCount: 3, timeProvider: clock);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        await backupService.BackupBeforeMigrationAsync();

        var remaining = Directory.GetFiles(backupDirectory).Select(Path.GetFileName).OrderDescending(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(3, remaining.Length, "Only complete snapshots remain after the prune.");
        AssertEx.Equal($"{BackupFilePrefix}20260101T000000000Z{BackupFileExtension}", remaining[0], "The fresh snapshot is kept under its final name.");
        AssertEx.Equal($"{BackupFilePrefix}20250103T000000000Z{BackupFileExtension}", remaining[2], "A leftover must not evict a good snapshot.");
        AssertEx.Equal(Path.Combine(backupDirectory, remaining[0]!), backupService.FindNewestSnapshot(), "The newest complete snapshot is the one a restore names.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BackupBeforeMigrationAsync_WhenBudgetRunsOut_SkipsAndLeavesNoFile(bool afterTheCopy)
    {
        var databasePath = GetDatabasePath(afterTheCopy ? "budget-after.sqlite" : "budget-before.sqlite");
        await SeedRawDataAsync(databasePath);

        // The budget timer fires at a point the test picks: as the snapshot connection opens (before VACUUM INTO runs) or as it
        // closes (the copy is complete and only the rename is left). Both must end with no snapshot and no temporary file.
        var clock = new TriggerableTimeProvider();
        var trigger = new BudgetTrigger(clock, afterTheCopy);
        await using var serviceProvider = BuildServiceProvider(databasePath, timeProvider: clock, chatInterceptor: trigger);

        await serviceProvider.GetRequiredService<INodeDbBackupService>().BackupBeforeMigrationAsync();

        AssertEx.Equal(expected: 1, clock.FiredCount, "The budget timer must have been armed and fired inside the snapshot.");
        var backupDirectory = Path.Combine(_rootPath, "backups");
        AssertEx.Empty(Directory.GetFiles(backupDirectory), "A snapshot over its budget is skipped and leaves no file.");
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_WhenFreeSpaceTooLow_SkipsSnapshot()
    {
        var databasePath = GetDatabasePath("low-space.sqlite");
        await SeedRawDataAsync(databasePath);

        // One free byte: far below the database size, so the free-space guard must skip instead of half-writing a copy.
        var freeSpace = Substitute.For<IFreeSpaceProbe>();
        freeSpace.GetAvailableFreeBytes(Arg.Any<string>()).Returns(1L);
        await using var serviceProvider = BuildServiceProvider(databasePath, freeSpaceProbe: freeSpace);

        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();
        await backupService.BackupBeforeMigrationAsync();

        AssertEx.Empty(Directory.GetFiles(Path.Combine(_rootPath, "backups")), "Too little free space skips the snapshot instead of filling the disk.");
        AssertEx.Equal(NodeDbAutomaticBackupOutcome.Skipped, backupService.LastAutomaticBackup.Outcome);
    }

    [Test]
    public async Task BackupBeforeMigrationAsync_PrunesToRetainCount()
    {
        var databasePath = GetDatabasePath("prune.sqlite");
        await SeedRawDataAsync(databasePath);

        var backupDirectory = Path.Combine(_rootPath, "backups");
        Directory.CreateDirectory(backupDirectory);

        // Four pre-existing snapshots, all older than the fake clock so the fresh one sorts newest.
        var older = new[]
        {
            "20250101T000000000Z",
            "20250102T000000000Z",
            "20250103T000000000Z",
            "20250104T000000000Z"
        };
        foreach (var stamp in older)
        {
            await File.WriteAllTextAsync(Path.Combine(backupDirectory, $"{BackupFilePrefix}{stamp}{BackupFileExtension}"), "stale");
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
        await SeedRawDataAsync(databasePath);

        // Plant a FILE where the backup directory is expected: Directory.CreateDirectory then throws IOException, exercising
        // the swallow-and-continue failure policy.
        var collidingPath = Path.Combine(_rootPath, "backup-collision");
        await File.WriteAllTextAsync(collidingPath, "not a directory");

        await using var serviceProvider = BuildServiceProvider(databasePath, backupDirectoryOverride: collidingPath);
        var backupService = serviceProvider.GetRequiredService<INodeDbBackupService>();

        // Must return normally — a backup failure never blocks migration/startup. (An exception here fails the test.)
        await backupService.BackupBeforeMigrationAsync();

        AssertEx.True(File.Exists(collidingPath), "The colliding file should be left untouched.");
        AssertEx.False(Directory.Exists(collidingPath), "The failed backup must not have replaced the file with a directory.");
        AssertEx.Equal(NodeDbAutomaticBackupOutcome.Failed, backupService.LastAutomaticBackup.Outcome);
        AssertEx.False(backupService.LastAutomaticBackup.Error!.Contains(_rootPath, StringComparison.Ordinal), "The recorded reason carries no path.");
    }

    private ServiceProvider BuildServiceProvider(string databasePath,
        int retainCount = 3,
        string? backupDirectoryOverride = null,
        TimeProvider? timeProvider = null,
        Action<NodeDbBackupOptions>? configure = null,
        IFreeSpaceProbe? freeSpaceProbe = null,
        IInterceptor? chatInterceptor = null)
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
        services.AddDbContext<NodeChatDbContext>(options =>
        {
            options.UseSqlite(connectionString);
            if (chatInterceptor is not null)
            {
                options.AddInterceptors(chatInterceptor);
            }
        });
        services.AddDbContext<NodeIdentityDbContext>(options => options.UseSqlite(connectionString,
            static sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable)));
        services.AddOptions<NodeDbBackupOptions>()
                .Configure(options =>
                {
                    options.BackupDirectory = backupDirectoryOverride;
                    configure?.Invoke(options);
                });
        // The retain count is a node setting now, read per backup.
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetNodeDbBackupRetainCountAsync(Arg.Any<CancellationToken>()).Returns(retainCount);
        services.AddSingleton(runtimeSettings);
        services.AddSingleton(freeSpaceProbe ?? new DriveInfoFreeSpaceProbe());
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
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE "probe" ("Id" INTEGER NOT NULL PRIMARY KEY, "Value" TEXT NOT NULL);
                              INSERT INTO "probe" ("Id", "Value") VALUES (1, 'seed');
                              """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> ProbeTableExistsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'probe';";
        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture) > 0;
    }

    private sealed class FixedNodeDataDirectory : INodeDataDirectory
    {
        public FixedNodeDataDirectory(string root)
        {
            Root = root;
        }

        public string Root { get; }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }
    }

    /// <summary>A fixed clock whose timers fire only when <see cref="FireAll" /> is called, so the snapshot budget runs out on cue.</summary>
    private sealed class TriggerableTimeProvider : TimeProvider
    {
        private readonly List<TriggerableTimer> _timers = [];

        public int FiredCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TriggerableTimer(callback, state);
            _timers.Add(timer);
            return timer;
        }

        public void FireAll()
        {
            foreach (var timer in _timers.Where(static timer => !timer.IsDisposed).ToList())
            {
                FiredCount++;
                timer.Fire();
            }
        }

        private sealed class TriggerableTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public TriggerableTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            public bool IsDisposed { get; private set; }

            public void Fire()
            {
                _callback(_state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                return true;
            }

            public void Dispose()
            {
                IsDisposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Holds an on-demand snapshot inside its connection open until the test releases it.</summary>
    private sealed class SnapshotGate : DbConnectionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task;
        }
    }

    /// <summary>Runs the budget out as a node-chat connection opens (before the copy) or closes (after it), the only connections the budget covers.</summary>
    private sealed class BudgetTrigger : DbConnectionInterceptor
    {
        private readonly TriggerableTimeProvider _clock;
        private readonly bool _afterTheCopy;

        public BudgetTrigger(TriggerableTimeProvider clock, bool afterTheCopy)
        {
            _clock = clock;
            _afterTheCopy = afterTheCopy;
        }

        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (!_afterTheCopy)
            {
                _clock.FireAll();
            }

            return Task.CompletedTask;
        }

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            if (_afterTheCopy)
            {
                _clock.FireAll();
            }

            return ValueTask.FromResult(result);
        }
    }
}
