namespace XE_Local_AI_Engine.Tests.Hosting;

using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Process-level regression for the downgrade commands' load-bearing placement before ordinary startup migrations.
///     Each child receives a database whose migration history is intentionally absent; a command that drifted below
///     <c>MigrateAsync</c> would create that history table and fail these assertions even if its exit code still looked
///     correct.
/// </summary>
[NotInParallel]
public sealed class KnowledgeDowngradeCliIntegrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-downgrade-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Commands_ExitBeforeStartupMigrations_AndReturnDocumentedCodes()
    {
        Directory.CreateDirectory(_rootPath);
        var compatibleDatabase = Path.Combine(_rootPath, "compatible.sqlite");
        var conflictingDatabase = Path.Combine(_rootPath, "conflicting.sqlite");
        var missingDatabase = Path.Combine(_rootPath, "missing.sqlite");
        await CreateCompatiblePreMigrationDatabaseAsync(compatibleDatabase).ConfigureAwait(false);
        await CreateConflictingPreMigrationDatabaseAsync(conflictingDatabase).ConfigureAwait(false);

        var compatible = await RunCommandAsync(DesktopLaunch.KnowledgeDowngradePreflightArgument, compatibleDatabase, "compatible-node")
            .ConfigureAwait(false);
        AssertEx.Equal(0, compatible.ExitCode, compatible.Output);
        await AssertPreMigrationSchemaUntouchedAsync(compatibleDatabase, expectedSentinel: "compatible").ConfigureAwait(false);

        var conflicting = await RunCommandAsync(DesktopLaunch.KnowledgeDowngradePreflightArgument, conflictingDatabase, "conflicting-node")
            .ConfigureAwait(false);
        AssertEx.Equal(3, conflicting.ExitCode, conflicting.Output);
        await AssertPreMigrationSchemaUntouchedAsync(conflictingDatabase, expectedSentinel: "conflicting").ConfigureAwait(false);

        var exported = await RunCommandAsync(DesktopLaunch.KnowledgeDowngradeExportArgument, conflictingDatabase, "export-node")
            .ConfigureAwait(false);
        AssertEx.Equal(3, exported.ExitCode, exported.Output);
        await AssertPreMigrationSchemaUntouchedAsync(conflictingDatabase, expectedSentinel: "conflicting").ConfigureAwait(false);
        AssertEx.Equal(1,
            Directory.EnumerateFiles(Path.Combine(_rootPath, "export-node", "backups", "knowledge-downgrade"), "*.sqlite").Count(),
            "The explicit export command must produce exactly one snapshot before reporting the compatibility block.");

        var failed = await RunCommandAsync(DesktopLaunch.KnowledgeDowngradePreflightArgument, missingDatabase, "missing-node")
            .ConfigureAwait(false);
        AssertEx.Equal(1, failed.ExitCode, failed.Output);
        AssertEx.False(File.Exists(missingDatabase), "A failed read-only preflight must not create the missing database.");
    }

    private static async Task CreateCompatiblePreMigrationDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sentinel(value TEXT NOT NULL); INSERT INTO sentinel(value) VALUES ('compatible');";
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task CreateConflictingPreMigrationDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              CREATE TABLE sentinel(value TEXT NOT NULL);
                              INSERT INTO sentinel(value) VALUES ('conflicting');
                              CREATE TABLE knowledge_documents(
                                  document_id TEXT NOT NULL,
                                  content_hash TEXT NOT NULL,
                                  collection_id TEXT NOT NULL);
                              INSERT INTO knowledge_documents(document_id, content_hash, collection_id)
                              VALUES
                                  ('11111111-1111-1111-1111-111111111111', 'duplicate', 'COLLECTION-A'),
                                  ('22222222-2222-2222-2222-222222222222', 'duplicate', 'COLLECTION-B');
                              """;
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task AssertPreMigrationSchemaUntouchedAsync(string databasePath, string expectedSentinel)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sentinel;";
        AssertEx.Equal(expectedSentinel, Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('__EFMigrationsHistory', '__EFMigrationsHistory_Identity');";
        AssertEx.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture),
            "Downgrade commands must exit before either startup migration context changes the database.");
    }

    private async Task<CommandResult> RunCommandAsync(string commandArgument, string databasePath, string nodeDirectoryName)
    {
        var nodeDirectory = Path.Combine(_rootPath, nodeDirectoryName);
        Directory.CreateDirectory(nodeDirectory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = new Process
        {
            StartInfo = CreateStartInfo(commandArgument, databasePath, nodeDirectory)
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("The knowledge downgrade CLI process could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = string.Concat(await standardOutput.ConfigureAwait(false), Environment.NewLine, await standardError.ConfigureAwait(false));
        return new CommandResult(process.ExitCode, output);
    }

    private static ProcessStartInfo CreateStartInfo(string commandArgument, string databasePath, string nodeDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(typeof(Program).Assembly.Location);
        startInfo.ArgumentList.Add(commandArgument);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        startInfo.Environment["ConnectionStrings__node-sqlite"] = $"Data Source={databasePath}";
        startInfo.Environment["NodeData__Directory"] = nodeDirectory;
        startInfo.Environment["WorkerNode__NodeName"] = "knowledge-downgrade-cli-test";
        startInfo.Environment["XE_NODE_SQLITE_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray());
        return startInfo;
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
