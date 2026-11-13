namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Scheduler-specific retention sweeper: deletes <c>scheduled_job_runs</c> rows, and their cascaded events, older
///     than the <c>SchedulerHistoryRetentionDays</c> node setting, read per sweep.
/// </summary>
/// <remarks>
///     It sweeps on the <see cref="SchedulerOptions.RetentionSweepIntervalMinutes" /> cadence and is kept separate from
///     the chat <c>RetentionSweeperService</c>, so scheduler history can evolve its own retention policy. Run rows
///     stamp <c>CreatedAtUtc</c> in unix-milliseconds, so the cutoff is computed in ms.
/// </remarks>
public sealed class SchedulerHistoryRetentionService : BackgroundService
{
    private readonly ILogger<SchedulerHistoryRetentionService> _logger;
    private readonly SchedulerOptions _options;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public SchedulerHistoryRetentionService(IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IOptions<SchedulerOptions> options,
        INodeRuntimeSettings runtimeSettings,
        ILogger<SchedulerHistoryRetentionService> logger)
    {
        ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        _serviceScopeFactory = serviceScopeFactory;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        _runtimeSettings = runtimeSettings;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await _runtimeSettings.GetSchedulerEnabledAsync(stoppingToken))
        {
            // Scheduler disabled → no jobs fire and no new history accrues; nothing to sweep.
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.RetentionSweepIntervalMinutes);
        using var timer = new PeriodicTimer(interval, _timeProvider);

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
                _logger.LogWarning(exception, "Scheduler history retention sweep failed.");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var runStore = scope.ServiceProvider.GetRequiredService<IScheduledJobRunStore>();
        var retentionDays = await _runtimeSettings.GetSchedulerHistoryRetentionDaysAsync(cancellationToken);

        var cutoffUtc = _timeProvider.GetUtcNow()
                                     .AddDays(-retentionDays)
                                     .ToUnixTimeMilliseconds();

        var deletedRunCount = await runStore.SweepOlderThanAsync(cutoffUtc, cancellationToken);

        if (deletedRunCount > 0)
        {
            _logger.LogInformation("Scheduler history retention sweep deleted {DeletedRunCount} run(s) older than {RetentionDays} day(s).",
                deletedRunCount,
                retentionDays);
        }
    }
}
