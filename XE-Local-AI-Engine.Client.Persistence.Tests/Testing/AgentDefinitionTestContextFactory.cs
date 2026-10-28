namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
///     Builds <see cref="NodeChatDbContext" /> test instances while keeping EF's internal service-provider count flat.
/// </summary>
/// <remarks>
///     A naive <c>new DbContextOptionsBuilder().AddInterceptors(new ...)</c> per context builds a distinct internal
///     provider and trips EF's twenty-provider <c>ManyServiceProvidersCreatedWarning</c> (an error in this solution).
///     One shared pair of interceptors lets EF reuse a single cached provider; the warning is also ignored as a
///     backstop, being a performance advisory irrelevant to short-lived test contexts.
/// </remarks>
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

    /// <summary>
    ///     Creates an encrypted context with one extra interceptor wired, for tests that inject a failure into a save
    ///     and assert the surrounding transaction rolled everything back.
    /// </summary>
    public static NodeChatDbContext Create(string databasePath, INodeSqliteKeyHolder keyHolder, IInterceptor extraInterceptor)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .AddInterceptors(SaveChangesInterceptor, MaterializationInterceptor, extraInterceptor)
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
