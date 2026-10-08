namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Persistence;

/// <summary>
///     The staged restore applied at start: it re-checks the snapshot, swaps it in and keeps the replaced database with its WAL,
///     and any bad marker, snapshot or link leaves the database untouched and the marker in place.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeDbRestoreStagingTests : IDisposable
{
    private const string SnapshotName = "node-chat-20260101T000000000Z.sqlite";
    private const string SetAsideName = "node.sqlite.prerestore-20260203T040506Z";
    private const string EarlierStamp = "20250101T000000Z";

    private static readonly DateTimeOffset Now = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> NoHistory = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-restore-staging-" + Guid.NewGuid().ToString("N"));

    public NodeDbRestoreStagingTests()
    {
        Directory.CreateDirectory(BackupDirectory);
    }

    private string BackupDirectory => Path.Combine(_rootPath, "backups");

    private string DatabasePath => Path.Combine(_rootPath, "node.sqlite");

    private string SnapshotPath => Path.Combine(BackupDirectory, SnapshotName);

    private string MarkerPath => Path.Combine(BackupDirectory, NodeDbRestoreStaging.MarkerFileName);

    private string EarlierAside => Path.Combine(_rootPath, $"node.sqlite.prerestore-{EarlierStamp}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ApplyPending_WithALiveWalDatabase_KeepsTheWalWithTheSetAsideCopy_AndTheRestoredDatabaseOpensClean()
    {
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateLiveWalDatabaseAsync(DatabasePath, "uncheckpointed");
        AssertEx.True(File.Exists(DatabasePath + "-wal"), "Precondition: the live database carries an uncheckpointed WAL.");
        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.Null(result.Error);
        AssertEx.Null(result.Warning);
        AssertEx.True(result.Applied);
        var setAside = AssertEx.NotNull(result.SetAsidePath);
        AssertEx.Equal(Path.Combine(_rootPath, SetAsideName), setAside);
        AssertEx.True(File.Exists(setAside + "-wal"), "The WAL goes aside with its database.");
        AssertEx.False(File.Exists(DatabasePath + "-wal") || File.Exists(DatabasePath + "-shm"), "No stale sidecar may meet the restored database.");
        AssertEx.False(File.Exists(MarkerPath), "An applied restore is not applied twice.");
        AssertEx.Equal("uncheckpointed", await ReadProbeAsync(setAside), "The kept copy still holds the change that lived only in its WAL.");
        AssertEx.Equal("snapshot", await ReadProbeAsync(DatabasePath));
        AssertEx.Equal("ok", await QuickCheckAsync(DatabasePath));
    }

    [Test]
    public async Task ApplyPending_AfterAnInterruptedAttempt_ReusesTheRecordedStamp_SoTheDatabaseAndItsWalStayTogether()
    {
        // An earlier attempt at T1 recorded its stamp, moved node.sqlite aside and died before its WAL; this start runs at Now (T2).
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateLiveWalDatabaseAsync(DatabasePath, "uncheckpointed");
        File.Move(DatabasePath, EarlierAside);
        await WriteStampedMarkerAsync(EarlierStamp);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.Null(result.Error);
        AssertEx.True(result.Applied);
        AssertEx.Equal(EarlierAside, result.SetAsidePath, "The restore reports the T1 copy it replaced, not a new T2 name.");
        AssertEx.True(File.Exists(EarlierAside + "-wal"), "The orphaned WAL joins the database it belongs to.");
        AssertEx.False(File.Exists(Path.Combine(_rootPath, SetAsideName)) || File.Exists(Path.Combine(_rootPath, SetAsideName + "-wal")),
            "No second stamp splits the pair.");
        AssertEx.False(File.Exists(DatabasePath + "-wal"));
        AssertEx.Equal("uncheckpointed", await ReadProbeAsync(EarlierAside));
        AssertEx.Equal("snapshot", await ReadProbeAsync(DatabasePath));
        AssertEx.Equal("ok", await QuickCheckAsync(DatabasePath));
    }

    [Test]
    public async Task ApplyPending_WhenTheCopyFails_PutsTheDatabaseAndItsWalBack()
    {
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateLiveWalDatabaseAsync(DatabasePath, "uncheckpointed");
        // A folder where the copy goes makes the exclusive create throw after the database and its WAL went aside.
        Directory.CreateDirectory(DatabasePath + ".restoring");
        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied);
        AssertEx.NotNull(result.Error);
        AssertEx.Null(result.SetAsidePath, "The rollback emptied the aside slot.");
        AssertEx.True(File.Exists(DatabasePath + "-wal"), "The WAL is back beside its database.");
        AssertEx.False(File.Exists(Path.Combine(_rootPath, SetAsideName)) || File.Exists(Path.Combine(_rootPath, SetAsideName + "-wal")));
        AssertEx.Equal("uncheckpointed", await ReadProbeAsync(DatabasePath), "The database reads its WAL-only change again.");
        AssertEx.True(File.Exists(MarkerPath));
    }

    [Test]
    public async Task ApplyPending_WhenASidecarCannotGoAside_RefusesToInstall_AndNamesTheFileItCouldNotPutBack()
    {
        // A WAL already sits under the stamped aside name, so the live WAL cannot move there and would stay beside the target.
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateLiveWalDatabaseAsync(DatabasePath, "uncheckpointed");
        await File.WriteAllTextAsync(EarlierAside + "-wal", "an earlier WAL");
        await WriteStampedMarkerAsync(EarlierStamp);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied);
        var error = AssertEx.NotNull(result.Error);
        AssertEx.True(error.Contains(EarlierAside + "-wal", StringComparison.Ordinal), "A failed move-back is named, not swallowed: " + error);
        AssertEx.Equal("an earlier WAL", await File.ReadAllTextAsync(EarlierAside + "-wal"), "Nothing aside is overwritten.");
        AssertEx.Equal("uncheckpointed", await ReadProbeAsync(DatabasePath), "The snapshot never meets the live WAL; the database is back.");
        AssertEx.True(File.Exists(MarkerPath));
    }

    [Test]
    [Arguments("-wal")]
    [Arguments("-shm")]
    public async Task ApplyPending_WhenASidecarIsADirectory_RefusesToInstall_AndPutsTheDatabaseBack(string suffix)
    {
        // A sidecar that is not a file cannot be moved aside, and SQLite would trip over it beside the restored database.
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateDatabaseAsync(DatabasePath, "live");
        Directory.CreateDirectory(DatabasePath + suffix);
        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied, "The snapshot must never be installed beside a sidecar.");
        AssertEx.True(result.Error!.Contains("-wal or -shm", StringComparison.Ordinal), result.Error);
        AssertEx.Null(result.SetAsidePath, "The rollback put the database back.");
        AssertEx.True(Directory.Exists(DatabasePath + suffix));
        AssertEx.False(File.Exists(Path.Combine(_rootPath, SetAsideName)));
        AssertEx.True(File.Exists(MarkerPath));
        Directory.Delete(DatabasePath + suffix);
        AssertEx.Equal("live", await ReadProbeAsync(DatabasePath));
    }

    [Test]
    public async Task ApplyPending_AfterAnInterruptedAttempt_WhenTheSnapshotIsNowRefused_NamesTheAsidePath_AndDeletesNothing()
    {
        await CreateDatabaseAsync(SnapshotPath, "snapshot", futureMigrationId: "20991231000000_FromANewerBuild");
        await CreateLiveWalDatabaseAsync(DatabasePath, "uncheckpointed");
        File.Move(DatabasePath, EarlierAside);
        File.Move(DatabasePath + "-wal", EarlierAside + "-wal");
        await WriteStampedMarkerAsync(EarlierStamp);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied);
        AssertEx.True(result.Error!.Contains("20991231000000_FromANewerBuild", StringComparison.Ordinal), result.Error);
        AssertEx.Equal(EarlierAside, result.SetAsidePath, "The hint must say where the database is, not that it was left in place.");
        AssertEx.Equal("uncheckpointed", await ReadProbeAsync(EarlierAside), "The aside database and its WAL are untouched.");
        AssertEx.True(File.Exists(SnapshotPath));
        AssertEx.True(File.Exists(MarkerPath));
        AssertEx.False(File.Exists(DatabasePath));
    }

    [Test]
    public async Task ApplyPending_WithoutAMarker_DoesNothing()
    {
        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied);
        AssertEx.Null(result.Error);
    }

    [Test]
    public async Task ApplyPending_WhenTheSnapshotIsMissing_FailsAndChangesNothing()
    {
        await CreateDatabaseAsync(DatabasePath, "live");
        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        await AssertUntouchedAsync(result);
    }

    [Test]
    public async Task ApplyPending_WhenTheSnapshotRecordsAnUnknownMigration_FailsAndChangesNothing()
    {
        // Staged by a newer build or changed after staging: the start checks again with the migrations this binary ships.
        await CreateDatabaseAsync(SnapshotPath, "snapshot", futureMigrationId: "20991231000000_FromANewerBuild");
        await CreateDatabaseAsync(DatabasePath, "live");
        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        await AssertUntouchedAsync(result);
        AssertEx.True(result.Error!.Contains("20991231000000_FromANewerBuild", StringComparison.Ordinal), result.Error);
    }

    [Test]
    [Arguments("../node-chat-20260101T000000000Z.sqlite")]
    [Arguments("..\\node-chat-20260101T000000000Z.sqlite")]
    [Arguments("node.sqlite")]
    public async Task ApplyPending_WhenTheMarkerNamesAPath_FailsAndChangesNothing(string name)
    {
        await CreateDatabaseAsync(DatabasePath, "live");
        await CreateDatabaseAsync(Path.Combine(_rootPath, SnapshotName), "outside the backups folder");
        await File.WriteAllTextAsync(MarkerPath, $$"""{"SnapshotName":{{JsonSerializer.Serialize(name)}}}""");

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        await AssertUntouchedAsync(result);
    }

    [Test]
    [Arguments("snapshot")]
    [Arguments("backups")]
    [Arguments("restoring")]
    public async Task ApplyPending_WhenALinkStandsInThePath_FailsAndChangesNothing(string linked)
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Creating a symbolic link needs Developer Mode or elevation on Windows; the link refusal is proven on Linux.");
            return;
        }

        var outside = Path.Combine(_rootPath, "outside");
        Directory.CreateDirectory(outside);
        await CreateDatabaseAsync(Path.Combine(outside, SnapshotName), "outside");
        await CreateDatabaseAsync(DatabasePath, "live");
        switch (linked)
        {
            case "snapshot":
                File.CreateSymbolicLink(SnapshotPath, Path.Combine(outside, SnapshotName));
                break;
            case "backups":
                Directory.Delete(BackupDirectory);
                Directory.CreateSymbolicLink(BackupDirectory, outside);
                break;
            default:
                await CreateDatabaseAsync(SnapshotPath, "snapshot");
                File.CreateSymbolicLink(DatabasePath + ".restoring", Path.Combine(outside, "target.sqlite"));
                break;
        }

        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        await AssertUntouchedAsync(result);
        AssertEx.False(File.Exists(Path.Combine(outside, "target.sqlite")), "Nothing is written through a link.");
    }

    [Test]
    public async Task ApplyPending_WhenOnlyTheMarkerCannotBeDeleted_ReportsAppliedWithAWarning()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("The undeletable marker is made with a read-only Unix directory mode, which Windows does not have.");
            return;
        }

        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateDatabaseAsync(DatabasePath, "live");
        // Already stamped, so the apply needs no marker rewrite and only the final delete meets the read-only folder.
        await WriteStampedMarkerAsync("20260203T040506Z");
        File.SetUnixFileMode(BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (CanCreateFileIn(BackupDirectory))
        {
            Skip.Test("BLOCKED: a privileged process ignores the read-only directory mode this test relies on.");
            return;
        }

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.True(result.Applied, "The snapshot is installed; the marker is the only thing left.");
        AssertEx.True(result.Installed);
        AssertEx.Null(result.Error);
        AssertEx.NotNull(result.Warning);
        AssertEx.Equal("snapshot", await ReadProbeAsync(DatabasePath));
        AssertEx.True(File.Exists(MarkerPath));
    }

    [Test]
    public async Task ApplyPending_WhenTheMarkerCannotBeDeleted_AndNothingWasSetAside_StopsTheStartInsteadOfContinuing()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("The undeletable marker is made with a read-only Unix directory mode, which Windows does not have.");
            return;
        }

        // No live database: the install leaves no set-aside copy, so a surviving marker would look like a new restore next time.
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await WriteStampedMarkerAsync("20260203T040506Z");
        File.SetUnixFileMode(BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        if (CanCreateFileIn(BackupDirectory))
        {
            Skip.Test("BLOCKED: a privileged process ignores the read-only directory mode this test relies on.");
            return;
        }

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied, "The start must stop: nothing lets the next start recognise the finished restore.");
        AssertEx.True(result.Installed, "The snapshot is the live database all the same.");
        AssertEx.True(AssertEx.NotNull(result.Error).Contains("could not be deleted", StringComparison.Ordinal), result.Error);
        AssertEx.Equal("snapshot", await ReadProbeAsync(DatabasePath));
        AssertEx.True(File.Exists(MarkerPath));
    }

    [Test]
    public async Task ApplyPending_RecordsTheStampBeforeTheFirstMove_AndPutsTheDatabaseBackWhenTheCopyFails()
    {
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await CreateDatabaseAsync(DatabasePath, "live");
        // A folder where the copy goes makes the exclusive create fail after the database has gone aside.
        Directory.CreateDirectory(DatabasePath + ".restoring");
        await NodeDbRestoreStaging.WriteMarkerAsync(BackupDirectory, SnapshotName, Now, CancellationToken.None);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        await AssertUntouchedAsync(result);
        AssertEx.Null(result.SetAsidePath, "The rollback put the database back, so nothing is aside.");
        using var marker = JsonDocument.Parse(await File.ReadAllTextAsync(MarkerPath));
        AssertEx.Equal("20260203T040506Z", marker.RootElement.GetProperty("SetAsideStamp").GetString(), "The stamp is recorded before anything moves.");
    }

    [Test]
    public async Task ApplyPending_WhenTheDatabaseIsAlreadyAside_AndTheSnapshotIsGone_NamesTheAsidePath()
    {
        const string earlier = "20250101T000000Z";
        var earlierAside = Path.Combine(_rootPath, $"node.sqlite.prerestore-{earlier}");
        await CreateDatabaseAsync(earlierAside, "live");
        await WriteStampedMarkerAsync(earlier);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied);
        AssertEx.NotNull(result.Error);
        AssertEx.Equal(earlierAside, result.SetAsidePath, "The hint must say where the database is, not that it was left in place.");
        AssertEx.True(File.Exists(MarkerPath));
    }

    [Test]
    public async Task ApplyPending_WhenAnEarlierStartInstalledTheRestore_DoesNotApplyAgain_AndClearsTheMarker()
    {
        // An earlier start installed the snapshot but could not delete the marker: the database and its stamped aside copy both exist.
        const string earlier = "20250101T000000Z";
        var earlierAside = Path.Combine(_rootPath, $"node.sqlite.prerestore-{earlier}");
        await CreateDatabaseAsync(earlierAside, "live");
        await CreateDatabaseAsync(DatabasePath, "restored");
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        await WriteStampedMarkerAsync(earlier);

        var result = await NodeDbRestoreStaging.ApplyPendingAsync(BackupDirectory, DatabasePath, Now, CancellationToken.None);

        AssertEx.False(result.Applied, "A finished restore is not applied twice.");
        AssertEx.Null(result.Error, "A leftover marker never stops the start.");
        AssertEx.NotNull(result.Warning);
        AssertEx.False(File.Exists(MarkerPath));
        AssertEx.Equal("restored", await ReadProbeAsync(DatabasePath), "The database written since the restore is kept.");
        AssertEx.Equal("live", await ReadProbeAsync(earlierAside));
        AssertEx.False(File.Exists(Path.Combine(_rootPath, SetAsideName)));
    }

    [Test]
    [Arguments("node-chat-20260101T000000000Z.sqlite", true)]
    [Arguments("node-chat-.sqlite", false)]
    [Arguments("node-chat-x/y.sqlite", false)]
    [Arguments("node-chat-x\\y.sqlite", false)]
    [Arguments("node-chat-..x.sqlite", false)]
    [Arguments("node-chat-latest.sqlite", false)]
    [Arguments("node-chat-20260101T000000000Z.sqlite\n", false)]
    [Arguments("node-chat-20260101T000000000.sqlite", false)]
    [Arguments("node-chat-20260101T000000000Z.sqlite.tmp", false)]
    [Arguments("restore-pending.json", false)]
    public void IsValidSnapshotName_AcceptsOnlyTheWritersName(string name, bool expected)
    {
        AssertEx.Equal(expected, NodeDbRestoreStaging.IsValidSnapshotName(name));
    }

    [Test]
    public async Task CheckSnapshotIntegrity_PassesASqliteFile_AndNamesWhyAnotherFails()
    {
        await CreateDatabaseAsync(SnapshotPath, "snapshot");
        var bad = Path.Combine(BackupDirectory, "node-chat-20260102T000000000Z.sqlite");
        await File.WriteAllTextAsync(bad, "this is not a SQLite database at all");

        AssertEx.Null(await NodeDbRestoreStaging.CheckSnapshotIntegrityAsync(SnapshotPath, NoHistory, CancellationToken.None));
        AssertEx.NotNull(await NodeDbRestoreStaging.CheckSnapshotIntegrityAsync(bad, NoHistory, CancellationToken.None));
    }

    [Test]
    public async Task CheckSnapshotIntegrity_RefusesASnapshotThatRecordsAMigrationThisBinaryDoesNotShip()
    {
        await CreateDatabaseAsync(SnapshotPath, "snapshot", futureMigrationId: null);
        var shipped = NodeDbRestoreStaging.ShippedMigrationIds;
        // Pinned to what EF itself declares for each context, so the attribute scan can neither miss nor misfile a migration.
        AssertEx.True(shipped[NodeDbRestoreStaging.ChatMigrationsHistoryTable].SetEquals(MigrationChainTests.DeclaredChatMigrations()),
            "The chat ids must be exactly EF's declared chat migrations.");
        AssertEx.True(shipped[NodeIdentityDbContext.IdentityMigrationsHistoryTable].SetEquals(MigrationChainTests.DeclaredIdentityMigrations()),
            "The identity ids must be exactly EF's declared identity migrations.");
        AssertEx.Equal(2, shipped.Count);
        AssertEx.Null(await NodeDbRestoreStaging.CheckSnapshotIntegrityAsync(SnapshotPath, shipped, CancellationToken.None), "Absent history tables are no reason to refuse.");

        await ExecuteAsync(SnapshotPath,
            """
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('20991231000000_FromANewerBuild', '10.0.0');
            """);

        var reason = AssertEx.NotNull(await NodeDbRestoreStaging.CheckSnapshotIntegrityAsync(SnapshotPath, shipped, CancellationToken.None));
        AssertEx.True(reason.Contains("20991231000000_FromANewerBuild", StringComparison.Ordinal), reason);
    }

    private async Task AssertUntouchedAsync(NodeDbRestoreApplyResult result)
    {
        AssertEx.False(result.Applied);
        AssertEx.NotNull(result.Error);
        AssertEx.Equal(MarkerPath, result.MarkerPath);
        AssertEx.Equal("live", await ReadProbeAsync(DatabasePath));
        AssertEx.False(File.Exists(Path.Combine(_rootPath, SetAsideName)), "A refused restore sets nothing aside.");
        AssertEx.True(File.Exists(MarkerPath), "A failed restore leaves the marker so the next start retries.");
    }

    private Task WriteStampedMarkerAsync(string stamp) =>
        File.WriteAllTextAsync(MarkerPath,
            JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SnapshotName"] = SnapshotName,
                ["SetAsideStamp"] = stamp
            }));

    private static bool CanCreateFileIn(string directory)
    {
        var probe = Path.Combine(directory, "write-probe");
        try
        {
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task CreateDatabaseAsync(string path, string value, string? futureMigrationId = null)
    {
        var history = futureMigrationId is null
            ? string.Empty
            : $"""
               CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
               INSERT INTO "__EFMigrationsHistory" VALUES ('{futureMigrationId}', '10.0.0');
               """;
        await ExecuteAsync(path, $"CREATE TABLE probe (value TEXT NOT NULL); INSERT INTO probe VALUES ('{value}'); {history}");
    }

    // Writes in WAL mode with checkpoints off and copies the files while the connection is still open, which is what a stopped
    // node leaves when its last checkpoint never ran: the change lives only in the -wal file.
    private static async Task CreateLiveWalDatabaseAsync(string path, string value)
    {
        var scratch = path + ".source";
        await using (var connection = new SqliteConnection($"Data Source={scratch}"))
        {
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                // The seed table is written before WAL mode, so the main file is a real database whose header says WAL.
                command.CommandText =
                    "CREATE TABLE seed (x INTEGER); PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE probe (value TEXT NOT NULL); INSERT INTO probe VALUES ($value);";
                command.Parameters.AddWithValue("$value", value);
                await command.ExecuteNonQueryAsync();
            }

            File.Copy(scratch, path);
            File.Copy(scratch + "-wal", path + "-wal");
            SqliteConnection.ClearPool(connection);
        }
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Test-owned literals only; nothing here comes from outside the test.
#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();

        // This file's pool only: clearing every pool would close a connection a parallel test is using.
        SqliteConnection.ClearPool(connection);
    }

    private static async Task<string?> ReadProbeAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM probe;";
        var value = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        SqliteConnection.ClearPool(connection);
        return value;
    }

    private static async Task<string?> QuickCheckAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var value = Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        SqliteConnection.ClearPool(connection);
        return value;
    }
}
