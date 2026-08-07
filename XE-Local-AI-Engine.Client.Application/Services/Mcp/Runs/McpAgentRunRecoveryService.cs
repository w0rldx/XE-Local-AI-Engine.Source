namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Fail-fast startup gate that repairs accounting and terminalizes every non-replayable prior claim.</summary>
internal sealed class McpAgentRunRecoveryService(
    IServiceScopeFactory scopeFactory,
    McpAgentRunMetrics metrics,
    TimeProvider timeProvider,
    ILogger<McpAgentRunRecoveryService> logger) : IHostedService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly McpAgentRunMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<McpAgentRunRecoveryService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<McpAgentRunAccountingService>()
                       .VerifyAndRepairAsync(cancellationToken).ConfigureAwait(false);

            var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();
            var count = await store.ReconcileInterruptedRunsAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken)
                                   .ConfigureAwait(false);
            if (count > 0)
            {
                await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
                _metrics.RecordRecovery("running_terminalized", count);
                _logger.LogWarning("Terminalized {Count} non-replayable durable MCP agent run claim(s) during startup recovery.", count);
            }
        }
        catch (Exception exception)
        {
            NodeSqliteContention.Record("raw", exception, _logger);
            _logger.LogCritical(exception, "Durable MCP agent run recovery failed; dispatch cannot start safely.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
