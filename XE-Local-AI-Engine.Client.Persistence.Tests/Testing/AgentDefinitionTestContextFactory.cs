namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Builds <see cref="NodeChatDbContext" /> test instances while keeping EF's internal service-provider count flat.
///     The agent-definition suite creates many short-lived SQLite databases; the naive
///     <c>new DbContextOptionsBuilder().AddInterceptors(new ...)</c> pattern builds a distinct internal provider per
///     context and, combined with the existing tests, trips EF's twenty-provider
///     <c>ManyServiceProvidersCreatedWarning</c> (which is configured as an error in this solution). Reusing one pair of
///     interceptor instances lets EF reuse a single cached internal provider for every encrypted context, and the
///     warning is ignored as a defensive backstop — it is a performance advisory irrelevant to short-lived test contexts.
/// </summary>
internal static class AgentDefinitionTestContextFactory
{
    private static readonly NodeEncryptionSaveChangesInterceptor SaveChangesInterceptor = new();
    private static readonly NodeEncryptionMaterializationInterceptor MaterializationInterceptor = new();

    /// <summary>Creates a context with the node encryption interceptors wired (encrypt on save, decrypt on materialize).</summary>
    public static NodeChatDbContext Create(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .AddInterceptors(SaveChangesInterceptor, MaterializationInterceptor)
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }

    /// <summary>Creates a context without encryption interceptors, for migration schema assertions that read raw columns.</summary>
    public static NodeChatDbContext CreateForMigration(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;

        return new NodeChatDbContext(options, keyHolder);
    }
}
