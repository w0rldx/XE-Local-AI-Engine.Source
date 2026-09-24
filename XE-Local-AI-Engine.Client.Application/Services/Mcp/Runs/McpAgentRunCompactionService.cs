namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Removes expired encrypted payloads while retaining keyed request-identity tombstones.</summary>
internal sealed class McpAgentRunCompactionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly McpAgentRunMetrics _metrics;
    private readonly McpAgentRunOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpAgentRunCompactionService> _logger;

    public McpAgentRunCompactionService(IServiceScopeFactory scopeFactory,
        McpAgentRunMetrics metrics,
        IOptions<McpAgentRunOptions> options,
        TimeProvider timeProvider,
        ILogger<McpAgentRunCompactionService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.CompactionIntervalMinutes), _timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CompactAsync(stoppingToken);
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
                if (!await timer.WaitForNextTickAsync(stoppingToken))
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
        var count = await store.CompactExpiredPayloadsAsync(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        if (count > 0)
        {
            await _metrics.RefreshAsync(store, CancellationToken.None);
            _metrics.RecordLifecycle("payload_compacted");
            _logger.LogInformation("Compacted expired payloads for {Count} durable MCP agent run(s).", count);
        }
    }
}
