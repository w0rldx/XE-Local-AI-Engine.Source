namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Retention sweeper for the metadata-only <c>agent_execution_logs</c> table, which is append-only and so grows
///     unbounded without one.
/// </summary>
/// <remarks>
///     It deletes rows older than <see cref="AgentExecutionLogRetentionOptions.RetentionDays" /> and, when set, trims
///     each agent to <see cref="AgentExecutionLogRetentionOptions.MaxRowsPerAgent" /> rows, on the configured
///     cadence. Each sweep runs on its own DI scope so the store's <c>DbContext</c> is never shared, and rows stamp
///     <c>CreatedAtUtc</c> in unix-milliseconds, so the cutoff is computed in milliseconds too.
/// </remarks>
public sealed class AgentExecutionLogRetentionService : BackgroundService
{
    private readonly ILogger<AgentExecutionLogRetentionService> _logger;
    private readonly AgentExecutionLogRetentionOptions _options;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public AgentExecutionLogRetentionService(IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IOptions<AgentExecutionLogRetentionOptions> options,
        ILogger<AgentExecutionLogRetentionService> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            // Retention disabled → leave telemetry untouched; nothing to sweep.
            return;
        }

        // Sweep once at startup rather than waiting a full interval, or a node down past a row's retention window
        // keeps it until the first tick. Best-effort: a failure is logged and the periodic loop still runs.
        try
        {
            await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Agent execution-log retention startup sweep failed.");
        }

        using var timer = new PeriodicTimer(_options.SweepInterval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Agent execution-log retention sweep failed.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var executionLogStore = scope.ServiceProvider.GetRequiredService<IAgentExecutionLogStore>();

        var cutoffEpochMs = _timeProvider.GetUtcNow()
                                         .AddDays(-_options.RetentionDays)
                                         .ToUnixTimeMilliseconds();

        var deletedByAge = await executionLogStore.DeleteOlderThanAsync(cutoffEpochMs, cancellationToken);

        var deletedByCap = 0;
        if (_options.MaxRowsPerAgent is { } maxRowsPerAgent && maxRowsPerAgent > 0)
        {
            deletedByCap = await executionLogStore.TrimToMaxPerAgentAsync(maxRowsPerAgent, cancellationToken);
        }

        if (deletedByAge > 0 || deletedByCap > 0)
        {
            _logger.LogInformation("Agent execution-log retention sweep deleted {DeletedByAge} log(s) older than {RetentionDays} day(s) and {DeletedByCap} over the per-agent cap.",
                deletedByAge,
                _options.RetentionDays,
                deletedByCap);
        }
    }
}
