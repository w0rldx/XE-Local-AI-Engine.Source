namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
///     Applies the node SQLite pragmas whenever EF Core opens a node database connection (migrations, EF queries/saves,
///     the health probe's <c>OpenConnectionAsync</c>).
/// </summary>
/// <remarks>
///     Raw-ADO opens on the same <see cref="DbConnection" /> bypass EF's interceptor pipeline and are covered separately
///     by the shared open-if-needed helpers, which route through <see cref="NodeSqlitePragmas.OpenAndConfigureAsync" />.
/// </remarks>
public sealed class NodeSqliteConnectionInterceptor : DbConnectionInterceptor
{
    private readonly NodeSqlitePragmaSettings _settings;
    private readonly ILogger<NodeSqliteConnectionInterceptor> _logger;

    public NodeSqliteConnectionInterceptor(NodeSqlitePragmaSettings settings, ILogger<NodeSqliteConnectionInterceptor> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _settings = settings;
        _logger = logger;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        NodeSqlitePragmas.Apply(connection, _settings, _logger);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await NodeSqlitePragmas.ApplyAsync(connection, _settings, _logger, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }
}
