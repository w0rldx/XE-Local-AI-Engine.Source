namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Verifies the persisted quota ledger and repairs it from authoritative run rows before dispatch begins.</summary>
internal sealed class McpAgentRunAccountingService(
    IMcpAgentRunStore store,
    McpAgentRunMetrics metrics,
    TimeProvider timeProvider,
    ILogger<McpAgentRunAccountingService> logger)
{
    private readonly IMcpAgentRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly McpAgentRunMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<McpAgentRunAccountingService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task VerifyAndRepairAsync(CancellationToken cancellationToken)
    {
        try
        {
            var verification = await _store.VerifyLedgerAsync(cancellationToken).ConfigureAwait(false);
            if (verification.IsConsistent)
            {
                await _metrics.RefreshAsync(_store, cancellationToken).ConfigureAwait(false);
                _metrics.RecordRecovery("accounting_verified");
                return;
            }

            _ = await _store.RebuildLedgerAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken)
                            .ConfigureAwait(false);
            var repaired = await _store.VerifyLedgerAsync(cancellationToken).ConfigureAwait(false);
            if (!repaired.IsConsistent)
            {
                throw new InvalidOperationException("The durable MCP run accounting ledger could not be reconstructed.");
            }

            await _metrics.RefreshAsync(_store, CancellationToken.None).ConfigureAwait(false);
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
