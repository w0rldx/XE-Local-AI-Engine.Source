namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Both migration chains apply cleanly from an empty database, and every migration the assembly declares is actually
///     recorded as applied. The per-migration suites each assert one migration's schema; this one asserts that no
///     migration in either chain is silently skipped or left pending — the failure mode a first launch on a fresh box
///     hits and nothing else here would catch.
/// </summary>
public sealed class MigrationChainTests
{
    [Test]
    public async Task ChatChain_AppliesEveryDeclaredMigrationFromEmpty()
    {
        var declared = DeclaredChatMigrations();
        AssertEx.True(declared.Count > 0, "The chat context must declare migrations.");

        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("chat-chain.sqlite").ConfigureAwait(false);

        var applied = await probe.AppliedMigrationsAsync(identityContext: false).ConfigureAwait(false);
        AssertEx.True(applied.SetEquals(declared),
            $"Applied chat migrations must be exactly the declared set; missing [{string.Join(", ", declared.Except(applied, StringComparer.Ordinal))}].");
    }

    [Test]
    public async Task IdentityChain_AppliesEveryDeclaredMigrationIntoItsOwnHistoryTable()
    {
        var declared = DeclaredIdentityMigrations();
        AssertEx.True(declared.Count > 0, "The identity context must declare migrations.");

        await using var probe = await MigrationSchemaProbe.MigrateIdentityAsync("identity-chain.sqlite").ConfigureAwait(false);

        var applied = await probe.AppliedMigrationsAsync(identityContext: true).ConfigureAwait(false);
        AssertEx.True(applied.SetEquals(declared),
            $"Applied identity migrations must be exactly the declared set; missing [{string.Join(", ", declared.Except(applied, StringComparer.Ordinal))}].");
    }

    private static IReadOnlyCollection<string> DeclaredChatMigrations()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
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

    private static IReadOnlyCollection<string> DeclaredIdentityMigrations()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);

        try
        {
            var options = new DbContextOptionsBuilder<NodeIdentityDbContext>()
                          .UseSqlite($"Data Source={Path.Combine(rootPath, "declared.sqlite")}",
                              static sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable))
                          .Options;

            using var context = new NodeIdentityDbContext(options);
            return [.. context.Database.GetService<IMigrationsAssembly>().Migrations.Keys];
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }
}
