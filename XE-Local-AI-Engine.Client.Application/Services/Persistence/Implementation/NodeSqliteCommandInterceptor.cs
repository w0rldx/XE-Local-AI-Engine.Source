namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Observes SQLITE_BUSY / SQLITE_LOCKED failures on EF-issued commands (migrations, EF queries/saves) and records
///     them on <see cref="NodeMetrics.SqliteBusyTotal" /> via <see cref="NodeSqliteContention" />. The raw-ADO chat-write
///     path is instrumented separately at its own boundary (it does not flow through EF's command pipeline).
/// </summary>
public sealed class NodeSqliteCommandInterceptor(ILogger<NodeSqliteCommandInterceptor> logger) : DbCommandInterceptor
{
    private readonly ILogger<NodeSqliteCommandInterceptor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
    {
        NodeSqliteContention.Record("ef", eventData.Exception, _logger);
        base.CommandFailed(command, eventData);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        NodeSqliteContention.Record("ef", eventData.Exception, _logger);
        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }
}
