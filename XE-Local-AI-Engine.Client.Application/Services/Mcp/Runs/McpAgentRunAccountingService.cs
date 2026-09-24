namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Verifies the persisted quota ledger and repairs it from authoritative run rows before dispatch begins.</summary>
internal sealed class McpAgentRunAccountingService
{
    private readonly IMcpAgentRunStore _store;
    private readonly McpAgentRunMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpAgentRunAccountingService> _logger;

    public McpAgentRunAccountingService(IMcpAgentRunStore store,
        McpAgentRunMetrics metrics,
        TimeProvider timeProvider,
        ILogger<McpAgentRunAccountingService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task VerifyAndRepairAsync(CancellationToken cancellationToken)
    {
        try
        {
            var verification = await _store.VerifyLedgerAsync(cancellationToken);
            if (verification.IsConsistent)
            {
                await _metrics.RefreshAsync(_store, cancellationToken);
                _metrics.RecordRecovery("accounting_verified");
                return;
            }

            _ = await _store.RebuildLedgerAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
            var repaired = await _store.VerifyLedgerAsync(cancellationToken);
            if (!repaired.IsConsistent)
            {
                throw new InvalidOperationException("The durable MCP run accounting ledger could not be reconstructed.");
            }

            await _metrics.RefreshAsync(_store, CancellationToken.None);
            _metrics.RecordRecovery("accounting_rebuilt");
            _logger.LogWarning("Rebuilt inconsistent durable MCP agent run accounting before dispatch started.");
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            throw;
        }
    }
}
