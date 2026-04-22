namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public sealed class NodeChatDbContextFactory : IDesignTimeDbContextFactory<NodeChatDbContext>
{
    private const string DefaultConnectionString = "Data Source=node-chat.design.db";

    public NodeChatDbContext CreateDbContext(string[] args)
    {
        _ = args;

        var optionsBuilder = new DbContextOptionsBuilder<NodeChatDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("XE_NODE_SQLITE_CONNECTION_STRING")
                               ?? DefaultConnectionString;

        optionsBuilder.UseSqlite(connectionString);

        return new NodeChatDbContext(optionsBuilder.Options, new NullNodeSqliteKeyHolder());
    }
}
