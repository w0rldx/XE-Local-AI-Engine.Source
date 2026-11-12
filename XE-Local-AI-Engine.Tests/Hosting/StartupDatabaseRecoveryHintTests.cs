namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A node database that cannot be opened or migrated must end startup with a message that names the newest
///     pre-migration snapshot and how to restore it, because the desktop shell shows nothing but the stderr tail.
/// </summary>
[NotInParallel]
[Category(TestCategories.Integration)]
public sealed class StartupDatabaseRecoveryHintTests : IDisposable
{
    private static readonly string[] LiveNodeState = ["node-settings.json", "cloud-credentials.enc"];

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-recovery-hint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task NotADatabase_FailsStartupNamingTheNewestSnapshotAndTheMoveAside()
    {
        var dataDirectory = Path.Combine(_rootPath, "node");
        var backupDirectory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "node-chat-20250101T000000000Z.sqlite"), "older");
        var newestSnapshot = Path.Combine(backupDirectory, "node-chat-20260101T000000000Z.sqlite");
        await File.WriteAllTextAsync(newestSnapshot, "newest");

        // What a sync-tool conflict copy or a torn copy leaves behind: a file SQLite rejects with SQLITE_NOTADB.
        var databasePath = Path.Combine(_rootPath, "node.sqlite");
        await File.WriteAllTextAsync(databasePath, new string('x', 4096));

        using var standardError = new StringWriter();
        var start = await StartHostAsync(databasePath, dataDirectory, standardError);
        var output = standardError.ToString();

        AssertEx.Null(start.App, output);
        AssertEx.Equal(expected: 9, start.ExitCode, output);
        AssertEx.True(output.Contains("is damaged or is not a SQLite database", StringComparison.Ordinal), output);
        AssertEx.True(output.Contains("node.sqlite.broken", StringComparison.Ordinal), output);
        AssertEx.True(output.Contains(newestSnapshot, StringComparison.Ordinal), output);
        foreach (var artifact in LiveNodeState)
        {
            AssertEx.True(File.Exists(Path.Combine(_rootPath, "content", artifact)), $"Startup moved {artifact} out of the content root.");
        }
    }

    [Test]
    public async Task DatabaseMigratedByANewerVersion_IsRefusedBeforeAnySnapshotOrMigration_WithTheDowngradeHint()
    {
        var dataDirectory = Path.Combine(_rootPath, "node");
        var backupDirectory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var olderSnapshot = Path.Combine(backupDirectory, "node-chat-20260101T000000000Z.sqlite");
        await File.WriteAllTextAsync(olderSnapshot, "older than the data");

        // Only the history a newer build left: without the guard this version would migrate over it and start.
        const string futureMigration = "20991231000000_FromANewerBuild";
        var databasePath = Path.Combine(_rootPath, "node.sqlite");
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL); "
                                  + "INSERT INTO \"__EFMigrationsHistory\" VALUES ('" + futureMigration + "', '99.0.0');";
            await command.ExecuteNonQueryAsync();
            SqliteConnection.ClearPool(connection);
        }

        var before = await File.ReadAllBytesAsync(databasePath);

        using var standardError = new StringWriter();
        var start = await StartHostAsync(databasePath, dataDirectory, standardError);
        var output = standardError.ToString();

        AssertEx.Null(start.App, output);
        AssertEx.Equal(Program.DatabaseMigrationFailedExitCode, start.ExitCode, output);
        AssertEx.True(output.Contains(Program.DescribeDatabaseDowngrade(databasePath, futureMigration), StringComparison.Ordinal), output);
        AssertEx.True(output.Contains(futureMigration, StringComparison.Ordinal) && output.Contains("before the upgrade", StringComparison.Ordinal), output);
        // The refused binary never starts, so it cannot stage a restore: the hint must spell out the copy by hand.
        AssertEx.True(output.Contains("cannot stage a restore", StringComparison.Ordinal), output);
        AssertEx.True(output.Contains($"delete '{databasePath}-wal' and '{databasePath}-shm'", StringComparison.Ordinal), output);
        AssertEx.True(output.Contains($"to '{databasePath}'.", StringComparison.Ordinal), output);
        AssertEx.False(output.Contains(olderSnapshot, StringComparison.Ordinal), "A snapshot older than the data must not be offered blindly.");
        var after = await File.ReadAllBytesAsync(databasePath);
        AssertEx.True(before.AsSpan().SequenceEqual(after), "The refused database must be left byte for byte.");
        AssertEx.Equal(1, Directory.GetFiles(backupDirectory).Length, "No pre-migration snapshot may be taken of a refused database.");
    }

    private async Task<ProgramStartResult> StartHostAsync(string databasePath, string dataDirectory, TextWriter standardError)
    {
        var webRoot = Path.Combine(_rootPath, "wwwroot");
        Directory.CreateDirectory(webRoot);

        // A scratch copy of the Client content root, never the real one, with the node state a live dev host keeps
        // there: the host must leave it in place (a differing NodeData:Directory would otherwise migrate it away).
        var contentRoot = Path.Combine(_rootPath, "content");
        Directory.CreateDirectory(contentRoot);
        foreach (var settings in Directory.EnumerateFiles(TestServerWebAppFactory.ResolveClientContentRoot(), "appsettings*.json"))
        {
            File.Copy(settings, Path.Combine(contentRoot, Path.GetFileName(settings)));
        }

        foreach (var artifact in LiveNodeState)
        {
            await File.WriteAllTextAsync(Path.Combine(contentRoot, artifact), "sentinel");
        }

        await TestServerWebAppFactory.HostStartupLock.WaitAsync();
        try
        {
            return await Program.CreateAppAsync([], new ProgramAppCustomization
            {
                ContentRootPath = contentRoot,
                WebRootPath = webRoot,
                StandardError = standardError,
                Configuration = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:node-sqlite"] = $"Data Source={databasePath}",
                    ["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray()),
                    ["NodeData:Directory"] = dataDirectory,
                    ["EntityFramework:ServiceProviderCaching"] = "false"
                },
                ConfigureBuilder = builder =>
                {
                    builder.WebHost.UseTestServer();

                    // The product NodeDataDirectory would migrate the content root's node state into the differing
                    // data dir, which Dispose deletes. The fake pins the same Root with no migration.
                    builder.Services.RemoveAll<INodeDataDirectory>();
                    builder.Services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(dataDirectory));
                }
            });
        }
        finally
        {
            TestServerWebAppFactory.HostStartupLock.Release();
        }
    }

    [Test]
    public void IsDatabaseFailure_MatchesADatabaseExceptionAnywhereInTheChain()
    {
        AssertEx.True(Program.IsDatabaseFailure(new SqliteException("file is not a database", 26)));
        AssertEx.True(Program.IsDatabaseFailure(new InvalidOperationException("wrapped", new SqliteException("disk I/O error", 10))));
    }

    [Test]
    public void IsDatabaseFailure_LeavesFileOptionsAndContainerFailuresToPropagate()
    {
        // These surface inside the migration pass without the database failing: options and DI resolve there first, and the canvas
        // staging step writes its own file. Exit 9 with a "restore the snapshot" hint would misreport them.
        AssertEx.False(Program.IsDatabaseFailure(new IOException("disk full")));
        AssertEx.False(Program.IsDatabaseFailure(new UnauthorizedAccessException("read-only")));
        AssertEx.False(Program.IsDatabaseFailure(new OptionsValidationException("WorkSessions", typeof(object), ["MaxParkedSeconds is too large."])));
        AssertEx.False(Program.IsDatabaseFailure(new InvalidOperationException("Unable to resolve service for type 'IFoo'.")));
        AssertEx.False(Program.IsDatabaseFailure(new HostAbortedException()));
    }
}
