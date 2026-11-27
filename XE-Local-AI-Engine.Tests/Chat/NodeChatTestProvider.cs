namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The service provider a chat persistence fixture builds over the node database file it owns.</summary>
internal static class NodeChatTestProvider
{
    /// <summary>
    ///     A provider over a node chat database at <paramref name="databasePath" />, recreated from the EF model unless
    ///     <paramref name="resetDatabase" /> is false.
    /// </summary>
    /// <param name="pooled">True opens the bare path-only connection string; false opens <see cref="NodeChatTestDatabase.ConnectionString" />.</param>
    /// <param name="keyHolder">The scoped key holder factory; null registers <see cref="NullNodeSqliteKeyHolder" />.</param>
    /// <param name="resetDatabase">False keeps the file's rows, so fresh connections reopen what an earlier provider wrote.</param>
    public static async Task<ServiceProvider> BuildAsync(string databasePath,
        bool pooled,
        Func<IServiceProvider, INodeSqliteKeyHolder>? keyHolder = null,
        bool resetDatabase = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = pooled ? $"Data Source={databasePath}" : NodeChatTestDatabase.ConnectionString(databasePath);
        var services = new ServiceCollection();
        services.AddScoped(keyHolder ?? (static _ => new NullNodeSqliteKeyHolder()));
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        if (resetDatabase)
        {
            await using var scope = provider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();
        }

        return provider;
    }
}
