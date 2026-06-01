namespace XE_Local_AI_Engine.Client.BackgroundServices;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Scheduler-specific retention sweeper. Deletes <c>scheduled_job_runs</c> rows (and their cascaded events) older
///     than <see cref="SchedulerOptions.HistoryRetentionDays" />, on a <see cref="SchedulerOptions.RetentionSweepIntervalMinutes" />
///     cadence. Kept separate from the chat <see cref="RetentionSweeperService" /> so scheduler history can evolve its own
///     retention policy. Run rows stamp <c>CreatedAtUtc</c> in unix-milliseconds, so the cutoff is computed in ms.
/// </summary>
public sealed class SchedulerHistoryRetentionService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly SchedulerOptions _options;
    private readonly ILogger<SchedulerHistoryRetentionService> _logger;

    public SchedulerHistoryRetentionService(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IOptions<SchedulerOptions> options,
        ILogger<SchedulerHistoryRetentionService> logger)
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
            // Scheduler disabled → no jobs fire and no new history accrues; nothing to sweep.
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.RetentionSweepIntervalMinutes);
        using var timer = new PeriodicTimer(interval, _timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
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
                await SweepAsync(stoppingToken).ConfigureAwait(false);
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

        var cutoffUtc = _timeProvider.GetUtcNow()
                                     .AddDays(-_options.HistoryRetentionDays)
                                     .ToUnixTimeMilliseconds();

        var deletedRunCount = await runStore.SweepOlderThanAsync(cutoffUtc, cancellationToken).ConfigureAwait(false);

        if (deletedRunCount > 0)
        {
            _logger.LogInformation(
                "Scheduler history retention sweep deleted {DeletedRunCount} run(s) older than {RetentionDays} day(s).",
                deletedRunCount,
                _options.HistoryRetentionDays);
        }
    }
}
