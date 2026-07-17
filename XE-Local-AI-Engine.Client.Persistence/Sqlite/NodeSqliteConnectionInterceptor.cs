namespace XE_Local_AI_Engine.Client.Persistence.Sqlite;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
///     Applies the node SQLite pragmas whenever EF Core opens a node database connection (migrations, EF queries/saves,
///     the health probe's <c>OpenConnectionAsync</c>). Raw-ADO opens on the same <see cref="DbConnection" /> bypass EF's
///     interceptor pipeline and are covered separately by the shared open-if-needed helpers, which route through
///     <see cref="NodeSqlitePragmas.OpenAndConfigureAsync" />.
/// </summary>
public sealed class NodeSqliteConnectionInterceptor(NodeSqlitePragmaSettings settings, ILogger<NodeSqliteConnectionInterceptor> logger) : DbConnectionInterceptor
{
    private readonly NodeSqlitePragmaSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ILogger<NodeSqliteConnectionInterceptor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        NodeSqlitePragmas.Apply(connection, _settings, _logger);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await NodeSqlitePragmas.ApplyAsync(connection, _settings, _logger, cancellationToken).ConfigureAwait(false);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }
}
