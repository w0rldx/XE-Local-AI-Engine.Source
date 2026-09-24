namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;

/// <summary>
///     The guard on <see cref="MigratedDatabaseTemplate" /> itself. Every suite that copies a template instead of
///     replaying the migration chain is trusting these three claims: the copy is indistinguishable from a replay, no
///     committed row is left behind in a write-ahead-log sidecar the copy does not carry, and a rebuilt migrations
///     assembly cannot be served a stale template.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class MigratedDatabaseTemplateTests : IDisposable
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
    public async Task ChatHeadTemplate_IsIndistinguishableFromAFromScratchMigratedDatabase()
    {
        await using var replayed = await MigrationSchemaProbe.MigrateChatAsync("chat-head-replayed.sqlite");
        await using var copied = await MigrationSchemaProbe.FromChatTemplateAsync("chat-head-copied.sqlite");

        var replayedMigrations = await replayed.AppliedMigrationsAsync(identityContext: false);
        var copiedMigrations = await copied.AppliedMigrationsAsync(identityContext: false);
        AssertEx.True(copiedMigrations.SetEquals(replayedMigrations),
            "A copied head template must record exactly the migrations a from-scratch replay records.");

        await AssertSameSchemaObjectsAsync(replayed, copied);
    }

    [Test]
    public async Task ChatTemplateAtAMigration_IsIndistinguishableFromAFromScratchMigrationToTheSameId()
    {
        // The penultimate declared migration, read from the assembly rather than pinned as a literal: a pinned id
        // would stop exercising a mid-chain stop the moment a new migration landed.
        var target = PenultimateDeclaredChatMigration();

        await using var replayed = await MigrationSchemaProbe.MigrateChatAsync("chat-at-replayed.sqlite", target);
        await using var copied = await MigrationSchemaProbe.FromChatTemplateAsync("chat-at-copied.sqlite", target);

        var replayedMigrations = await replayed.AppliedMigrationsAsync(identityContext: false);
        var copiedMigrations = await copied.AppliedMigrationsAsync(identityContext: false);
        AssertEx.True(copiedMigrations.SetEquals(replayedMigrations),
            $"A template stopped at {target} must record exactly the migrations a replay to {target} records.");
        AssertEx.True(!copiedMigrations.Contains(HeadDeclaredChatMigration()),
            "A template stopped short of head must not record the head migration — otherwise the tail no longer runs for real.");

        await AssertSameSchemaObjectsAsync(replayed, copied);
    }

    [Test]
    public async Task IdentityHeadTemplate_IsIndistinguishableFromAFromScratchMigratedDatabase()
    {
        await using var replayed = await MigrationSchemaProbe.MigrateIdentityAsync("identity-head-replayed.sqlite");
        await using var copied = await MigrationSchemaProbe.FromIdentityTemplateAsync("identity-head-copied.sqlite");

        var replayedMigrations = await replayed.AppliedMigrationsAsync(identityContext: true);
        var copiedMigrations = await copied.AppliedMigrationsAsync(identityContext: true);
        AssertEx.True(copiedMigrations.SetEquals(replayedMigrations),
            "A copied identity template must record exactly the migrations a from-scratch replay records.");

        await AssertSameSchemaObjectsAsync(replayed, copied);
    }

    [Test]
    public async Task PublishedTemplate_CarriesTheMigrationsAssemblyKeyAndNoWriteAheadLogSidecar()
    {
        var templatePath = await MigratedDatabaseTemplate.ChatHeadPathAsync();

        var moduleVersionId = typeof(NodeChatDbContext).Assembly.ManifestModule.ModuleVersionId.ToString("N", CultureInfo.InvariantCulture);
        AssertEx.True(Path.GetFileName(templatePath).Contains(moduleVersionId, StringComparison.Ordinal),
            $"The template file name must carry the migrations assembly's module version id ({moduleVersionId}), "
            + "or a rebuilt assembly would be served the previous build's schema.");

        AssertNoSidecars(templatePath, "the published template");
    }

    [Test]
    public async Task CopiedDatabase_HasNoWriteAheadLogSidecarBeforeItIsOpened()
    {
        var databasePath = Path.Combine(_rootPath, "copy-only", "node.sqlite");

        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        AssertEx.True(File.Exists(databasePath), "The copy must land at the requested path, creating its directory.");
        AssertNoSidecars(databasePath, "a copied database");
    }

    [Test]
    public async Task CopyChatAtAsync_RejectsATargetThatIsNotADeclaredMigrationId()
    {
        // The mistake this catches is a template keyed on something variable — a file name, a GUID. Each distinct
        // target costs a full chain replay to build, so it has to fail on the first call, not after dozens of them.
        var undeclared = Guid.NewGuid().ToString("N");

        var failure = await AssertEx
                            .ThrowsAsync<InvalidOperationException>(async () =>
                                await MigratedDatabaseTemplate.CopyChatAtAsync(Path.Combine(_rootPath, "undeclared.sqlite"), undeclared));

        AssertEx.True(failure.Message.Contains(undeclared, StringComparison.Ordinal),
            "The message must name the rejected id, or the caller cannot tell which call site is wrong.");
    }

    [Test]
    public async Task CopyChatHeadAsync_RefusesToOverwriteADatabaseThatIsAlreadyThere()
    {
        // A second copy onto one path would replace the main file and leave the first database's -wal/-shm beside it.
        var databasePath = Path.Combine(_rootPath, "double-copy", "node.sqlite");
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);

        _ = await AssertEx.ThrowsAsync<IOException>(async () => await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath));
    }

    [Test]
    public async Task CopyChatAtAsync_RejectsABlankTargetMigrationId()
    {
        // The guard that keeps a caller from silently keying a template on nothing and getting the head chain.
        _ = await AssertEx.ThrowsAsync<ArgumentException>(async () => await MigratedDatabaseTemplate.CopyChatAtAsync(Path.Combine(_rootPath, "blank.sqlite"), "   "));
    }

    [Test]
    public void SweepStaleTemplates_DeletesAnotherBuildsTemplateAndKeepsTheCurrentOne()
    {
        var current = Path.Combine(_rootPath, "current-key-chat-head.sqlite");
        var stale = Path.Combine(_rootPath, "stale-key-chat-head.sqlite");
        var scratch = Path.Combine(_rootPath, "build-in-flight");
        _ = Directory.CreateDirectory(scratch);
        File.WriteAllText(current, "current");
        File.WriteAllText(stale, "stale");

        MigratedDatabaseTemplate.SweepStaleTemplates(_rootPath, "current-key");

        AssertEx.False(File.Exists(stale), "A template keyed on another build's module version id must be swept.");
        AssertEx.True(File.Exists(current), "The current build's template must survive the sweep.");
        AssertEx.True(Directory.Exists(scratch), "A concurrent same-build process's scratch directory must survive the sweep.");
    }

    private static void AssertNoSidecars(string databasePath, string subject)
    {
        AssertEx.False(File.Exists(databasePath + "-wal"),
            $"A -wal sidecar next to {subject} means a single-file copy would lose committed rows.");
        AssertEx.False(File.Exists(databasePath + "-shm"),
            $"A -shm sidecar next to {subject} means the last connection to it was never closed; the file being in "
            + "write-ahead-log mode is expected and harmless once it is.");
    }

    private static async Task AssertSameSchemaObjectsAsync(MigrationSchemaProbe replayed, MigrationSchemaProbe copied)
    {
        var replayedObjects = await SchemaObjectsAsync(replayed.DatabasePath);
        var copiedObjects = await SchemaObjectsAsync(copied.DatabasePath);

        AssertEx.True(copiedObjects.SetEquals(replayedObjects),
            "The copy and the replay must expose the same tables, indexes and triggers; "
            + $"only in the replay [{string.Join(", ", replayedObjects.Except(copiedObjects, StringComparer.Ordinal))}], "
            + $"only in the copy [{string.Join(", ", copiedObjects.Except(replayedObjects, StringComparer.Ordinal))}].");
    }

    private static async Task<IReadOnlySet<string>> SchemaObjectsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // The DDL text is in the compared string, not just the object name: without it a copy and a replay could
        // agree on every table, index and trigger name while differing in a column.
        command.CommandText = "SELECT type || ':' || name || ':' || COALESCE(sql, '') FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';";

        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            _ = names.Add(reader.GetString(ordinal: 0));
        }

        SqliteConnection.ClearPool(connection);
        return names;
    }

    private static string PenultimateDeclaredChatMigration()
    {
        var declared = DeclaredChatMigrations();
        AssertEx.True(declared.Count > 1, "The chat context must declare more than one migration for a mid-chain stop to mean anything.");
        return declared[^2];
    }

    private static string HeadDeclaredChatMigration()
    {
        return DeclaredChatMigrations()[^1];
    }

    private static IReadOnlyList<string> DeclaredChatMigrations()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(rootPath);
        using var keyHolder = new NullNodeSqliteKeyHolder();

        try
        {
            using var context = AgentDefinitionTestContextFactory.CreateForMigration(Path.Combine(rootPath, "declared.sqlite"), keyHolder);
            return [.. context.Database.GetService<IMigrationsAssembly>().Migrations.Keys];
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
