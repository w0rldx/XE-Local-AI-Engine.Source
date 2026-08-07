namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Removes expired encrypted payloads while retaining keyed request-identity tombstones.</summary>
internal sealed class McpAgentRunCompactionService(
    IServiceScopeFactory scopeFactory,
    McpAgentRunMetrics metrics,
    IOptions<McpAgentRunOptions> options,
    TimeProvider timeProvider,
    ILogger<McpAgentRunCompactionService> logger) : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    private readonly McpAgentRunMetrics _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    private readonly McpAgentRunOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<McpAgentRunCompactionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CompactionIntervalMinutes), _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CompactAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                NodeSqliteContention.Record("raw", exception, _logger);
                _logger.LogError(exception, "Durable MCP agent run payload compaction failed; the next interval will retry.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpAgentRunStore>();
        var count = await store.CompactExpiredPayloadsAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken)
                               .ConfigureAwait(false);
        if (count > 0)
        {
            await _metrics.RefreshAsync(store, CancellationToken.None).ConfigureAwait(false);
            _metrics.RecordLifecycle("payload_compacted");
            _logger.LogInformation("Compacted expired payloads for {Count} durable MCP agent run(s).", count);
        }
    }
}
