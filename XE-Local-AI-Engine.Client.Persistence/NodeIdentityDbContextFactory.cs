namespace XE_Local_AI_Engine.Client.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public sealed class NodeIdentityDbContextFactory : IDesignTimeDbContextFactory<NodeIdentityDbContext>
{
    private const string DefaultConnectionString = "Data Source=node-identity.design.db";

    public NodeIdentityDbContext CreateDbContext(string[] args)
    {
        _ = args;

        var optionsBuilder = new DbContextOptionsBuilder<NodeIdentityDbContext>();
        var connectionString = Environment.GetEnvironmentVariable("XE_NODE_SQLITE_CONNECTION_STRING")
                               ?? DefaultConnectionString;

        optionsBuilder.UseSqlite(connectionString,
            sqlite => sqlite.MigrationsHistoryTable(NodeIdentityDbContext.IdentityMigrationsHistoryTable));

        return new NodeIdentityDbContext(optionsBuilder.Options);
    }
}
